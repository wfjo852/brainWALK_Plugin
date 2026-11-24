using System.Text.Json;

namespace FemtoSkeletonAgent;

/// <summary>
/// 등록된 사람. Profile = 신체 치수(뼈 길이, mm) 8개. 0 = 아직 못 잰 항목.
/// 다시 나타났을 때 같은 사람인지 판단하는 데 씁니다.
/// </summary>
public sealed class Person
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Color { get; set; }
    public bool AutoNamed { get; set; } = true;
    public float[] Profile { get; set; } = new float[PersonRegistry.FeatureCount];
    public int Samples { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 사람 등록부.
///  - Body Tracking의 body id는 사람이 화면을 벗어났다 돌아오면 바뀝니다.
///  - 새 body가 나타나면 잠깐 체형(뼈 길이)을 재서, 지금 화면에 없는 등록된 사람 중 가장 비슷한 사람으로 이어 붙입니다.
///
/// v1.2 개선 (실제 기록에서 26명 중 24명이 체형 없이 등록된 문제):
///  - 관절 13개가 "모두 동시에" 보여야 재던 방식 → 부위별로 보이는 것만 잽니다 (다리가 가려져도 상체로 판단).
///  - 체형을 못 잰 채 시간이 지나도 새 사람으로 만들지 않습니다. 방금 나간 사람이 있으면 그 사람으로 이어 붙입니다.
///  - 방금(60초 안) 나간 사람은 기준을 너그럽게 적용합니다 (거리·자세에 따른 측정 오차 감안).
///  - 판단 근거를 reid.log 에 남깁니다 (%LocalAppData%\FemtoSkeleton).
/// </summary>
public sealed class PersonRegistry
{
    // 신체 치수 특징: (관절 A, 관절 B) 쌍들의 거리 평균. 좌우가 있는 부위는 보이는 쪽만으로도 잽니다.
    private static readonly (int a, int b)[][] Features =
    {
        new[] { (5, 12) },             // 0 어깨 너비
        new[] { (5, 6), (12, 13) },    // 1 위팔
        new[] { (6, 7), (13, 14) },    // 2 아래팔
        new[] { (18, 19), (22, 23) },  // 3 허벅지
        new[] { (19, 20), (23, 24) },  // 4 정강이
        new[] { (0, 3) },              // 5 몸통 (골반~목)
        new[] { (18, 22) },            // 6 골반 너비
        new[] { (3, 26) },             // 7 목~머리
    };
    public static readonly string[] FeatureNames = { "어깨", "위팔", "아래팔", "허벅지", "정강이", "몸통", "골반", "목머리" };
    public static int FeatureCount => 8;
    // 측정이 안정적인 부위일수록 크게 (골반 너비·목~머리는 짧고 흔들림이 커서 작게)
    private static readonly float[] Weights = { 1.0f, 1.0f, 0.9f, 1.0f, 0.9f, 1.3f, 0.4f, 0.4f };

    private const int MinSamplesPerFeature = 6;      // 부위마다 이만큼 재야 그 값을 믿음
    private const int MinFeaturesToMatch = 3;        // 비교에 쓸 부위가 최소 이만큼은 있어야 함
    private const int ReadyFrames = 20;              // 약 0.7초 재고 판단
    private const int MaxFramesToIdentify = 150;     // 5초까지 기다림 (그동안 "확인 중")
    private const float MatchThreshold = 0.08f;      // 오래전에 본 사람: 평균 오차 8% 이내
    private const float RecentThreshold = 0.13f;     // 방금 나간 사람: 13% 이내
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(60);
    private const int ColorCount = 6;

    private sealed class Track
    {
        public string? PersonId;
        public readonly List<float[]> Samples = new();
        public int Frames;
        public readonly float[] Current = NewUnknown(); // 이 body의 최근 치수 (지정/새 등록 시 사용)
        public readonly int[] CurrentCount = new int[8];
    }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _logPath;
    private readonly List<Person> _people = new();
    private readonly Dictionary<uint, Track> _tracks = new();
    private bool _peopleChanged = true;
    private DateTime _lastSave = DateTime.UtcNow;
    private bool _dirty;

    /// <summary>사람 목록이 바뀌면 호출 (JSON 메시지)</summary>
    public event Action<byte[]>? PeopleChanged;

    public PersonRegistry(string path)
    {
        _path = path;
        _logPath = Path.Combine(Path.GetDirectoryName(path)!, "reid.log");
        Load();
    }

    public static string DefaultPath(bool demo)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FemtoSkeleton");
        return Path.Combine(dir, demo ? "people-demo.json" : "people.json");
    }

    private static float[] NewUnknown()
    {
        var a = new float[8];
        Array.Fill(a, float.NaN);
        return a;
    }

    // ───────────── 프레임 처리 (카메라 스레드) ─────────────

    /// <summary>현재 프레임의 body들에 사람 ID를 붙여서 돌려줍니다 (아직 확인 중이면 null).</summary>
    public string?[] Process(IReadOnlyList<BodyData> bodies)
    {
        var result = new string?[bodies.Count];
        lock (_gate)
        {
            // 사라진 body 정리 → 그 사람은 다시 "화면에 없음" 상태가 되어 재식별 후보가 됨
            var present = bodies.Select(b => b.Id).ToHashSet();
            foreach (var gone in _tracks.Keys.Where(k => !present.Contains(k)).ToList())
            {
                var t = _tracks[gone];
                if (t.PersonId != null)
                {
                    _peopleChanged = true;
                    Log($"나감: {Find(t.PersonId)?.Name} (body {gone})");
                }
                _tracks.Remove(gone);
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (!_tracks.TryGetValue(b.Id, out var track))
                {
                    track = new Track();
                    _tracks[b.Id] = track;
                }
                track.Frames++;
                var feat = Measure(b.Joints);
                if (feat != null)
                {
                    for (int k = 0; k < feat.Length; k++)
                    {
                        if (float.IsNaN(feat[k])) continue;
                        track.CurrentCount[k]++;
                        track.Current[k] = float.IsNaN(track.Current[k]) ? feat[k] : track.Current[k] + (feat[k] - track.Current[k]) * 0.1f;
                    }
                }

                if (track.PersonId == null)
                {
                    if (feat != null) track.Samples.Add(feat);
                    if (ReadyToIdentify(track)) Identify(track, b.Id);
                }
                else
                {
                    var p = Find(track.PersonId);
                    if (p == null) { track.PersonId = null; track.Samples.Clear(); }
                    else
                    {
                        p.LastSeenUtc = DateTime.UtcNow;
                        if (feat != null) { Learn(p, feat, 0.02f); _dirty = true; }
                    }
                }
                result[i] = track.PersonId;
            }

            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 30 && _dirty) SaveLocked();
        }
        FlushPeopleChanged();
        return result;
    }

    private static bool ReadyToIdentify(Track t)
    {
        if (t.Frames >= MaxFramesToIdentify) return true;
        if (t.Frames < ReadyFrames) return false;
        return CountReliable(t.Samples) >= MinFeaturesToMatch + 1;
    }

    private static int CountReliable(List<float[]> samples)
    {
        int n = 0;
        for (int k = 0; k < 8; k++)
            if (samples.Count(s => !float.IsNaN(s[k])) >= MinSamplesPerFeature) n++;
        return n;
    }

    private void Identify(Track track, uint bodyId)
    {
        var profile = Median(track.Samples);   // 못 잰 부위는 NaN
        int measured = profile.Count(v => !float.IsNaN(v));
        var active = ActivePersonIds();
        var now = DateTime.UtcNow;

        // 후보: 지금 화면에 없는 등록된 사람
        var candidates = _people.Where(p => !active.Contains(p.Id))
            .Select(p => (p, d: Distance(profile, p.Profile, out int n), n, recent: now - p.LastSeenUtc < RecentWindow))
            .ToList();

        var reasons = string.Join(", ", candidates.Where(c => c.n > 0).OrderBy(c => c.d).Take(3)
            .Select(c => $"{c.p.Name} {c.d:P1}/{c.n}부위{(c.recent ? "/방금" : "")}"));
        var measuredText = string.Join(" ", Enumerable.Range(0, 8).Where(k => !float.IsNaN(profile[k])).Select(k => $"{FeatureNames[k]}{profile[k]:0}"));

        Person? chosen = null;
        string how = "";

        // 1) 체형 비교: 방금 나간 사람은 너그럽게
        var matches = candidates.Where(c => c.n >= MinFeaturesToMatch && c.d <= (c.recent ? RecentThreshold : MatchThreshold))
            .OrderBy(c => c.d - (c.recent ? 0.03f : 0f)).ToList();
        if (matches.Count > 0)
        {
            chosen = matches[0].p;
            how = $"체형 일치 {matches[0].d:P1}";
        }
        // 2) 체형으로 판단할 수 없을 때(잰 부위 부족 / 비교할 기록 없음): 방금 나간 사람이 있으면 그 사람
        else
        {
            var recent = candidates.Where(c => c.recent && (c.n < MinFeaturesToMatch || c.d <= RecentThreshold * 1.5f))
                .OrderByDescending(c => c.p.LastSeenUtc).ToList();
            bool cannotJudge = measured < MinFeaturesToMatch || recent.Any(c => c.n < MinFeaturesToMatch);
            if (recent.Count > 0 && cannotJudge)
            {
                chosen = recent[0].p;
                how = "체형을 충분히 못 재서 방금 나간 사람으로 판단";
            }
        }

        if (chosen != null)
        {
            track.PersonId = chosen.Id;
            Learn(chosen, profile, 0.25f);
            Console.WriteLine($"  다시 인식: {chosen.Name} ({how})");
            Log($"다시 인식: {chosen.Name} ← body {bodyId} | {how} | 잰 값: {measuredText} | 후보: {reasons}");
        }
        else
        {
            var p = CreatePerson(profile);
            track.PersonId = p.Id;
            Console.WriteLine($"  새 사람 등록: {p.Name}" + (reasons != "" ? $" (가장 비슷한: {reasons})" : ""));
            Log($"새 사람: {p.Name} ← body {bodyId} | 잰 값: {measuredText} ({measured}부위) | 후보: {(reasons == "" ? "없음" : reasons)}");
        }
        track.Samples.Clear();
        _peopleChanged = true;
        _dirty = true;
    }

    private Person CreatePerson(float[]? profile)
    {
        // "사람 N": 기존 자동 이름 중 가장 큰 번호 + 1
        int n = 1;
        foreach (var p in _people)
            if (p.Name.StartsWith("사람 ") && int.TryParse(p.Name.AsSpan(3), out var k) && k >= n) n = k + 1;

        // 색: 지금 화면에 있는 사람과 겹치지 않는 색 우선
        var active = ActivePersonIds();
        var used = _people.Where(p => active.Contains(p.Id)).Select(p => p.Color).ToHashSet();
        int color = Enumerable.Range(0, ColorCount).FirstOrDefault(c => !used.Contains(c), _people.Count % ColorCount);

        var person = new Person
        {
            Id = "p" + Guid.NewGuid().ToString("N")[..8],
            Name = $"사람 {n}",
            Color = color,
        };
        if (profile != null) Learn(person, profile, 1f);
        _people.Add(person);
        _dirty = true;
        return person;
    }

    /// <summary>부위별로 학습. NaN(못 잰 부위)은 건너뛰고, 처음 잰 부위는 그대로 저장.</summary>
    private static void Learn(Person p, float[] feat, float weight)
    {
        if (p.Profile.Length != 8)
        {
            var old = p.Profile;
            p.Profile = new float[8];
            if (old.Length == 8) Array.Copy(old, p.Profile, 8);
        }
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            if (float.IsNaN(feat[i]) || feat[i] <= 0) continue;
            p.Profile[i] = p.Profile[i] <= 0 ? feat[i] : p.Profile[i] + (feat[i] - p.Profile[i]) * weight;
            any = true;
        }
        if (any) p.Samples++;
    }

    // ───────────── 웹에서 오는 명령 ─────────────

    /// <summary>{"cmd":"rename","pid":"..","name":".."} / {"cmd":"delete","pid":".."} /
    /// {"cmd":"assign","track":3,"pid":".."} / {"cmd":"new","track":3} / {"cmd":"cleanup"}</summary>
    public void HandleCommand(JsonElement msg)
    {
        string cmd = msg.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        lock (_gate)
        {
            switch (cmd)
            {
                case "rename":
                {
                    var p = Find(Str(msg, "pid"));
                    var name = Str(msg, "name")?.Trim();
                    if (p == null || string.IsNullOrEmpty(name)) return;
                    if (name.Length > 30) name = name[..30];
                    Console.WriteLine($"  이름 변경: {p.Name} → {name}");
                    p.Name = name;
                    p.AutoNamed = false;
                    break;
                }
                case "delete":
                {
                    var p = Find(Str(msg, "pid"));
                    if (p == null) return;
                    _people.Remove(p);
                    // 지금 화면에 있던 사람이면 다시 확인
                    foreach (var t in _tracks.Values.Where(t => t.PersonId == p.Id)) { t.PersonId = null; t.Samples.Clear(); t.Frames = 0; }
                    Console.WriteLine($"  삭제: {p.Name}");
                    break;
                }
                case "cleanup":
                {
                    // 자동 등록("사람 N") 중 지금 화면에 없는 사람 정리. 직접 이름 붙인 사람은 남김.
                    var active = ActivePersonIds();
                    int removed = _people.RemoveAll(p => p.AutoNamed && !active.Contains(p.Id));
                    Console.WriteLine($"  자동 등록 정리: {removed}명 삭제");
                    Log($"자동 등록 정리: {removed}명 삭제");
                    break;
                }
                case "assign":
                {
                    // "이 사람은 사실 OO입니다" — 잘못 인식된 것을 바로잡기 (그 사람의 체형도 이걸로 보정)
                    if (!TryTrack(msg, out var track)) return;
                    var target = Find(Str(msg, "pid"));
                    if (target == null || track.PersonId == target.Id) return;

                    // 이미 다른 body에 붙어 있던 사람이면 그쪽은 다시 확인하도록
                    foreach (var t in _tracks.Values.Where(t => t.PersonId == target.Id)) { t.PersonId = null; t.Samples.Clear(); t.Frames = 0; }

                    var previous = Find(track.PersonId);
                    track.PersonId = target.Id;
                    Learn(target, track.Current, 0.6f);
                    // 잘못 자동 등록된 사람은 정리 (직접 이름을 붙인 사람은 남김)
                    if (previous != null && previous.AutoNamed) _people.Remove(previous);
                    Console.WriteLine($"  지정: body {Str(msg, "track")} → {target.Name}");
                    Log($"직접 지정: body {Str(msg, "track")} → {target.Name} (이전: {previous?.Name})");
                    break;
                }
                case "new":
                {
                    if (!TryTrack(msg, out var track)) return;
                    var p = CreatePerson((float[])track.Current.Clone());
                    track.PersonId = p.Id;
                    Console.WriteLine($"  새 사람으로 등록: {p.Name}");
                    break;
                }
                default:
                    return;
            }
            _peopleChanged = true;
            SaveLocked();
        }
        FlushPeopleChanged();
    }

    private bool TryTrack(JsonElement msg, out Track track)
    {
        track = null!;
        return msg.TryGetProperty("track", out var t) && t.TryGetUInt32(out var id) && _tracks.TryGetValue(id, out track!);
    }

    private static string? Str(JsonElement msg, string key) =>
        msg.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    // ───────────── 사람 목록 메시지 ─────────────

    public byte[] PeopleMessage()
    {
        lock (_gate) return BuildPeopleMessage();
    }

    private byte[] BuildPeopleMessage()
    {
        var trackOf = _tracks.Where(kv => kv.Value.PersonId != null).ToDictionary(kv => kv.Value.PersonId!, kv => kv.Key);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("type", "people");
            w.WriteStartArray("people");
            foreach (var p in _people.OrderByDescending(p => trackOf.ContainsKey(p.Id)).ThenByDescending(p => p.LastSeenUtc))
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("name", p.Name);
                w.WriteNumber("color", p.Color);
                w.WriteBoolean("auto", p.AutoNamed);
                if (trackOf.TryGetValue(p.Id, out var tid)) w.WriteNumber("track", tid); else w.WriteNull("track");
                w.WriteString("lastSeen", p.LastSeenUtc.ToString("o"));
                w.WriteNumber("measured", p.Profile.Count(v => v > 0));   // 체형을 잰 부위 수 (0~8)
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private void FlushPeopleChanged()
    {
        byte[]? msg = null;
        lock (_gate)
        {
            if (!_peopleChanged) return;
            _peopleChanged = false;
            msg = BuildPeopleMessage();
        }
        PeopleChanged?.Invoke(msg);
    }

    // ───────────── 치수 계산 ─────────────

    /// <summary>부위별 뼈 길이 (mm). 관절이 잘 안 보이는 부위는 NaN. 하나도 못 재면 null.</summary>
    public static float[]? Measure(float[] j)
    {
        var f = NewUnknown();
        bool any = false;
        for (int i = 0; i < Features.Length; i++)
        {
            float sum = 0;
            int n = 0;
            foreach (var (a, b) in Features[i])
            {
                if (j[a * 4 + 3] < 2 || j[b * 4 + 3] < 2) continue; // 이 쪽은 가려짐 → 반대쪽만으로
                float dx = j[a * 4] - j[b * 4], dy = j[a * 4 + 1] - j[b * 4 + 1], dz = j[a * 4 + 2] - j[b * 4 + 2];
                sum += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                n++;
            }
            if (n > 0) { f[i] = sum / n; any = true; }
        }
        return any ? f : null;
    }

    /// <summary>가중 평균 상대 오차 (0.05 = 5%). 둘 다 잰 부위만 비교하고, 비교한 부위 수를 n으로 돌려줍니다.</summary>
    public static float Distance(float[] a, float[] b, out int n)
    {
        float s = 0, w = 0;
        n = 0;
        if (b.Length != a.Length) return float.MaxValue;
        for (int i = 0; i < a.Length; i++)
        {
            if (float.IsNaN(a[i]) || float.IsNaN(b[i]) || a[i] <= 0 || b[i] <= 0) continue;
            s += Weights[i] * MathF.Abs(a[i] - b[i]) / ((a[i] + b[i]) * 0.5f);
            w += Weights[i];
            n++;
        }
        return n == 0 ? float.MaxValue : s / w;
    }

    /// <summary>부위별 중앙값. 충분히 못 잰 부위는 NaN.</summary>
    private static float[] Median(List<float[]> samples)
    {
        var m = NewUnknown();
        for (int i = 0; i < 8; i++)
        {
            var col = samples.Select(s => s[i]).Where(v => !float.IsNaN(v)).OrderBy(v => v).ToArray();
            if (col.Length >= MinSamplesPerFeature) m[i] = col[col.Length / 2];
        }
        return m;
    }

    // ───────────── 도우미 / 저장 / 기록 ─────────────

    private Person? Find(string? id) => id == null ? null : _people.FirstOrDefault(p => p.Id == id);
    private HashSet<string> ActivePersonIds() => _tracks.Values.Where(t => t.PersonId != null).Select(t => t.PersonId!).ToHashSet();

    private void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 1_000_000) File.Move(_logPath, _logPath + ".old", overwrite: true);
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* 기록 실패는 무시 */ }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<Person>>(File.ReadAllText(_path), JsonOpts);
            if (list != null)
            {
                foreach (var p in list.Where(p => !string.IsNullOrEmpty(p.Id)))
                {
                    if (p.Profile.Length != 8)   // 예전 형식(빈 배열) → 8칸, 0 = 아직 못 잰 부위
                    {
                        var fixedProfile = new float[8];
                        if (p.Profile.Length == 8) Array.Copy(p.Profile, fixedProfile, 8);
                        p.Profile = fixedProfile;
                    }
                    _people.Add(p);
                }
            }
            int noBody = _people.Count(p => p.Profile.All(v => v <= 0));
            Console.WriteLine($"  등록된 사람 {_people.Count}명 불러옴" + (noBody > 0 ? $" (체형 기록 없는 사람 {noBody}명)" : "") + $" ({_path})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  사람 목록을 읽지 못했습니다: {ex.Message}");
        }
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_people, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  사람 목록 저장 실패: {ex.Message}");
        }
    }
}
