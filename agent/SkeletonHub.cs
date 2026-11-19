using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FemtoSkeletonAgent;

/// <summary>
/// 한 사람의 스켈레톤. Joints = 32개 × (x, y, z, confidence). 좌표는 깊이 카메라 기준 mm (x 오른쪽, y 아래, z 앞).
/// Points2D = 32개 × (u, v): 컬러 영상 위 픽셀 좌표 (투영 불가면 -1). 영상이 없으면 null.
/// </summary>
public sealed record BodyData(uint Id, float[] Joints, float[]? Points2D = null);

/// <summary>
/// 연결된 브라우저들에게 데이터를 보내고, 브라우저에서 오는 명령(이름 변경 등)을 받는 허브.
///  - 프레임: 클라이언트마다 "최신 1개"만 유지 (느린 브라우저는 오래된 프레임을 건너뜀)
///  - 상태/사람 목록: 버리지 않고 순서대로 모두 전달
/// </summary>
public sealed class SkeletonHub
{
    public const int JointCount = 32;

    private sealed class Client
    {
        public readonly ConcurrentQueue<byte[]> Control = new();
        public byte[]? LatestFrame;
        public byte[]? LatestVideo;
        public volatile bool VideoOn = true;   // 3D 보기에서는 영상을 받지 않음
        public readonly SemaphoreSlim Signal = new(0);
        public void Wake() { if (Signal.CurrentCount == 0) Signal.Release(); }
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly PersonRegistry _registry;
    private byte[] _latestStatus = Encoding.UTF8.GetBytes("""{"type":"status","state":"starting","message":"시작 중..."}""");

    public SkeletonHub(PersonRegistry registry)
    {
        _registry = registry;
        _registry.PeopleChanged += msg => BroadcastControl(msg);
    }

    public string LatestStatusJson => Encoding.UTF8.GetString(Volatile.Read(ref _latestStatus));
    public int ClientCount => _clients.Count;

    /// <summary>영상을 원하는 브라우저가 있을 때만 영상 처리를 합니다 (CPU 절약).</summary>
    public bool WantsVideo => _clients.Values.Any(c => c.VideoOn);

    /// <summary>영상 프레임 (VideoPacket). 클라이언트마다 최신 1개만 유지.</summary>
    public void PublishVideo(byte[] packet)
    {
        foreach (var c in _clients.Values)
        {
            if (!c.VideoOn) continue;
            Volatile.Write(ref c.LatestVideo, packet);
            c.Wake();
        }
    }

    private static JsonWriterOptions WriterOpts => new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>상태 메시지 (state: starting | running | error, source: demo | camera)</summary>
    public void PublishStatus(string state, string message, string source)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "status");
            w.WriteString("state", state);
            w.WriteString("source", source);
            w.WriteString("message", message);
            w.WriteEndObject();
        }
        var bytes = buf.WrittenSpan.ToArray();
        Volatile.Write(ref _latestStatus, bytes);
        BroadcastControl(bytes);
        Console.WriteLine($"  [{state}] {message}");
    }

    /// <summary>스켈레톤 프레임 전송. 사람 식별은 브라우저 연결 여부와 상관없이 항상 처리합니다.</summary>
    public void PublishFrame(ulong timestampUs, IReadOnlyList<BodyData> bodies)
    {
        var personIds = _registry.Process(bodies);
        if (_clients.IsEmpty) return;

        var buf = new ArrayBufferWriter<byte>(512 + bodies.Count * 1600);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "frame");
            w.WriteNumber("t", timestampUs);
            w.WriteStartArray("bodies");
            for (int bi = 0; bi < bodies.Count; bi++)
            {
                var b = bodies[bi];
                w.WriteStartObject();
                w.WriteNumber("id", b.Id);
                if (personIds[bi] != null) w.WriteString("pid", personIds[bi]); else w.WriteNull("pid");
                w.WriteStartArray("j");
                for (int i = 0; i < b.Joints.Length; i++)
                {
                    // 4번째 값(신뢰도)은 정수, 좌표는 소수점 1자리
                    if (i % 4 == 3) w.WriteNumberValue((int)b.Joints[i]);
                    else w.WriteNumberValue(MathF.Round(b.Joints[i], 1));
                }
                w.WriteEndArray();
                if (b.Points2D != null)
                {
                    w.WriteStartArray("p2");
                    foreach (var v in b.Points2D) w.WriteNumberValue(MathF.Round(v, 1));
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var msg = buf.WrittenSpan.ToArray();
        foreach (var c in _clients.Values)
        {
            Volatile.Write(ref c.LatestFrame, msg);
            c.Wake();
        }
    }

    private byte[]? _latestCamera;

    /// <summary>
    /// 카메라 기울기 (깊이 카메라 좌표의 위쪽 단위 벡터). 브라우저가 아바타·3D를 수평으로 세우는 데 씁니다.
    /// {"type":"camera","up":[x,y,z],"source":"imu"|"demo"}
    /// </summary>
    public void PublishCamera(float[] up, string source)
    {
        var buf = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "camera");
            w.WriteStartArray("up");
            foreach (var v in up) w.WriteNumberValue(MathF.Round(v, 5));
            w.WriteEndArray();
            w.WriteString("source", source);
            w.WriteEndObject();
        }
        var bytes = buf.WrittenSpan.ToArray();
        Volatile.Write(ref _latestCamera, bytes);
        BroadcastControl(bytes);
    }

    /// <summary>열린 브라우저들에게 페이지 새로고침 요청 (웹 화면 파일이 바뀌었을 때)</summary>
    public void PublishReload() => BroadcastControl(Encoding.UTF8.GetBytes("""{"type":"reload"}"""));

    private void BroadcastControl(byte[] message)
    {
        foreach (var c in _clients.Values)
        {
            c.Control.Enqueue(message);
            c.Wake();
        }
    }

    public async Task ServeClientAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var client = new Client();
        // 접속 직후 현재 상태와 사람 목록
        client.Control.Enqueue(Volatile.Read(ref _latestStatus));
        client.Control.Enqueue(_registry.PeopleMessage());
        if (Volatile.Read(ref _latestCamera) is { } cam) client.Control.Enqueue(cam);
        client.Wake();
        _clients[id] = client;
        Console.WriteLine($"  브라우저 연결됨 (현재 {_clients.Count}개)");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoopAsync(ws, client, linked);
        try
        {
            while (!linked.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await client.Signal.WaitAsync(linked.Token);
                while (client.Control.TryDequeue(out var ctl))
                    await ws.SendAsync(ctl, WebSocketMessageType.Text, true, linked.Token);
                var video = Interlocked.Exchange(ref client.LatestVideo, null);
                if (video != null)
                    await ws.SendAsync(video, WebSocketMessageType.Binary, true, linked.Token);
                var frame = Interlocked.Exchange(ref client.LatestFrame, null);
                if (frame != null)
                    await ws.SendAsync(frame, WebSocketMessageType.Text, true, linked.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            linked.Cancel();
            try { await receiveTask; } catch { }
            _clients.TryRemove(id, out _);
            Console.WriteLine($"  브라우저 연결 해제 (현재 {_clients.Count}개)");
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }

    /// <summary>브라우저 → 에이전트 명령 수신 (JSON 텍스트)</summary>
    private async Task ReceiveLoopAsync(WebSocket ws, Client client, CancellationTokenSource linked)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, linked.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 64 * 1024) { ms.SetLength(0); continue; } // 비정상적으로 큰 메시지 무시
                if (!r.EndOfMessage) continue;

                try
                {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    // {"cmd":"video","on":false} — 이 브라우저의 영상 수신 켜기/끄기
                    if (root.TryGetProperty("cmd", out var c) && c.GetString() == "video")
                    {
                        client.VideoOn = !root.TryGetProperty("on", out var on) || on.ValueKind != JsonValueKind.False;
                        if (!client.VideoOn) client.LatestVideo = null;
                    }
                    else _registry.HandleCommand(root);
                }
                catch (JsonException) { /* 잘못된 메시지 무시 */ }
                ms.SetLength(0);
            }
        }
        catch { /* 연결 끊김 */ }
        finally { linked.Cancel(); }
    }
}
