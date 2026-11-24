// ─────────────────────────────────────────────────────────────
//  음성 합성 (TTS) — 에이전트가 Windows 한국어 음성으로 소리 파일(WAV)을 만들어 웹에 보냄
//
//  GET /tts?text=오른팔 올려&rate=-1   → audio/wav
//  GET /tts/status                    → { available, voice }
//
//  · 인터넷 없이 동작. 브라우저 음성(speechSynthesis)이 없거나 막혀 있어도 소리가 나옵니다.
//  · Windows SAPI(COM) 사용 → 별도 패키지 설치 필요 없음.
//    한국어 Windows에 기본 포함된 "Microsoft Heami"를 찾고, 없으면 설정에서 추가한 OneCore 한국어 음성을 찾습니다.
//  · 같은 문장은 한 번만 만들고 기억해 둠 (게임 명령은 대부분 반복).
// ─────────────────────────────────────────────────────────────
using System.Collections.Concurrent;

sealed class TtsService : IDisposable
{
    readonly BlockingCollection<(string Text, int Rate, TaskCompletionSource<byte[]?> Done)> _jobs = new();
    readonly ConcurrentDictionary<string, Task<byte[]?>> _cache = new();
    readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    const int MaxCache = 400;

    public string? VoiceName { get; private set; }
    public Task<bool> Ready => _ready.Task;

    public TtsService()
    {
        if (!OperatingSystem.IsWindows()) { _ready.TrySetResult(false); return; }
        // SAPI는 COM이라 전용 STA 스레드 하나에서만 사용
        var t = new Thread(Run) { IsBackground = true, Name = "TTS" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>문장 → WAV. 음성이 없으면 null</summary>
    public async Task<byte[]?> SynthAsync(string text, int rate)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 200) return null;
        if (!await Ready) return null;
        rate = Math.Clamp(rate, -10, 10);
        var key = rate + "|" + text;
        if (_cache.Count > MaxCache) _cache.Clear();
        var task = _cache.GetOrAdd(key, _ =>
        {
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Add((text, rate, tcs));
            return tcs.Task;
        });
        var wav = await task;
        if (wav == null) _cache.TryRemove(key, out _);
        return wav;
    }

    void Run()
    {
        dynamic voice;
        try
        {
            voice = Com("SAPI.SpVoice");
            var token = FindKoreanVoice(voice);
            if (token == null)
            {
                Console.WriteLine("  [음성] 한국어 Windows 음성을 찾지 못함 → 브라우저 음성을 사용합니다.");
                Console.WriteLine("         (Windows 설정 → 시간 및 언어 → 음성 → 음성 추가 → 한국어)");
                _ready.TrySetResult(false);
                return;
            }
            voice.Voice = token;
            VoiceName = (string)token.GetDescription(0);
            Console.WriteLine($"  [음성] {VoiceName}");
            _ready.TrySetResult(true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [음성] Windows 음성 사용 불가 ({e.Message}) → 브라우저 음성을 사용합니다.");
            _ready.TrySetResult(false);
            return;
        }

        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                dynamic stream = Com("SAPI.SpMemoryStream");
                dynamic fmt = stream.Format;
                fmt.Type = 22;                       // SAFT22kHz16BitMono
                stream.Format = fmt;
                voice.AudioOutputStream = stream;
                voice.Rate = job.Rate;
                voice.Speak(job.Text, 16);           // 16 = SVSFIsNotXML (동기 실행)
                byte[] pcm = (byte[])stream.GetData();
                job.Done.TrySetResult(Wav(pcm, 22050, 16, 1));
            }
            catch (Exception e)
            {
                Console.WriteLine($"  [음성] 만들기 실패: {e.Message}");
                job.Done.TrySetResult(null);
            }
        }
    }

    static dynamic Com(string progId)
    {
        var t = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException(progId + " 없음");
        return Activator.CreateInstance(t)!;
    }

    /// <summary>한국어(LCID 412) 음성: 기본 SAPI 목록 → 없으면 OneCore(설정에서 추가한 음성) 목록</summary>
    static dynamic? FindKoreanVoice(dynamic voice)
    {
        try
        {
            dynamic list = voice.GetVoices("Language=412", "");
            if ((int)list.Count > 0) return list.Item(0);
        }
        catch { /* 다음 방법 */ }
        try
        {
            dynamic cat = Com("SAPI.SpObjectTokenCategory");
            cat.SetId(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices", false);
            dynamic list = cat.EnumerateTokens("Language=412", "");
            if ((int)list.Count > 0) return list.Item(0);
        }
        catch { /* 없음 */ }
        return null;
    }

    static byte[] Wav(byte[] pcm, int sampleRate, int bits, int channels)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        int blockAlign = channels * bits / 8;
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * blockAlign); w.Write((short)blockAlign); w.Write((short)bits);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose() => _jobs.CompleteAdding();
}
