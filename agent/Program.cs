using System.Diagnostics;
using System.Net.WebSockets;
using FemtoSkeletonAgent;

// ─────────────────────────────────────────────────────────────
//  Femto Bolt Skeleton Agent
//  카메라 → Body Tracking → WebSocket(ws://localhost:PORT/ws) → 브라우저
//
//  실행 옵션
//    --demo            카메라 없이 가짜 스켈레톤으로 웹 테스트
//    --port 5000       포트 변경
//    --mode gpu        처리 모드: gpu(기본, Windows는 DirectML) | cpu
//    --no-browser      시작 시 브라우저 자동 실행 안 함
//    --viewer          게임 선택 화면 대신 뷰어(스켈레톤 화면)로 시작
//    --window          전체 화면이 아니라 창으로 열기
//    --no-lan          휴대폰 사진 올리기(QR) 끄기 → 이 PC에서만 접속
// ─────────────────────────────────────────────────────────────

var options = AgentOptions.Parse(args);
// 등록된 사람 목록 (데모와 실제 카메라는 따로 저장)
var registry = new PersonRegistry(PersonRegistry.DefaultPath(options.Demo));
var hub = new SkeletonHub(registry);

// 웹 화면 파일은 원본 폴더(agent\wwwroot)를 바로 사용합니다.
// → index.html 등을 고치면 다시 빌드할 필요 없이 열린 브라우저가 자동으로 새로고침됩니다.
var webRoot = WebRootLocator.Find();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot,
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// 같은 와이파이의 휴대폰이 사진을 올릴 수 있게 모든 네트워크에서 받음.
// 단, 다른 기기는 "사진 올리기" 페이지(/up/)와 올리기(POST /api/up)만 쓸 수 있음 (아래 문지기)
// --no-lan 이면 예전처럼 이 PC에서만 접속
builder.WebHost.UseUrls(options.Lan ? $"http://*:{options.Port}" : $"http://localhost:{options.Port}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 40 * 1024 * 1024);

var inbox = new PhotoInbox();
var app = builder.Build();

// 문지기: 이 PC가 아닌 곳(휴대폰)에서 온 요청은 사진 올리기만 허용
app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip == null || System.Net.IPAddress.IsLoopback(ip)) { await next(); return; }
    var path = ctx.Request.Path.Value ?? "";
    var allowed = (HttpMethods.IsGet(ctx.Request.Method) && (path == "/up" || path.StartsWith("/up/")))
               || (HttpMethods.IsPost(ctx.Request.Method) && path == "/api/up");
    if (!allowed) { ctx.Response.StatusCode = 404; return; }
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // 브라우저가 옛 파일을 들고 있지 않도록 항상 새로 확인
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

// 웹 화면 파일이 바뀌면 열린 브라우저에 "새로고침" 신호
using var webWatcher = WebRootLocator.Watch(webRoot, () =>
{
    Console.WriteLine("  웹 화면 파일 변경 감지 → 브라우저 새로고침");
    hub.PublishReload();
});

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.ServeClientAsync(ws, ctx.RequestAborted);
});

app.MapGet("/api/status", () => Results.Text(hub.LatestStatusJson, "application/json"));

// 음성 합성: Windows 한국어 음성으로 WAV를 만들어 보냄 (게임 음성 안내용, 인터넷 불필요)
using var tts = new TtsService();
app.MapGet("/tts/status", async () =>
{
    var ok = await tts.Ready.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result);
    return Results.Json(new { available = ok, voice = tts.VoiceName });
});
app.MapGet("/tts", async (string? text, int? rate) =>
{
    var wav = string.IsNullOrWhiteSpace(text) ? null : await tts.SynthAsync(text, rate ?? -1);
    return wav == null ? Results.StatusCode(503) : Results.File(wav, "audio/wav");
});

// ───────── 휴대폰 사진 받기 (사진 깨기) ─────────
// 게임 화면용 (이 PC에서만): QR 주소, QR 그림, 도착한 사진 목록/가져가기/지우기
app.MapGet("/api/up/info", () =>
{
    var url = options.Lan ? inbox.UploadUrl(options.Port) : null;
    return Results.Json(new { url, lan = options.Lan });
});
app.MapGet("/api/up/qr.svg", () =>
{
    var url = options.Lan ? inbox.UploadUrl(options.Port) : null;
    return url == null ? Results.NotFound() : Results.Text(PhotoInbox.QrSvg(url), "image/svg+xml");
});
app.MapGet("/api/up/list", () => Results.Json(inbox.List()));
app.MapGet("/api/up/{seq:int}", (int seq, HttpContext ctx) =>
{
    var item = inbox.Get(seq);
    if (item == null) return Results.NotFound();
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(item.Data, item.ContentType);
});
app.MapDelete("/api/up/{seq:int}", (int seq) => { inbox.Remove(seq); return Results.Ok(); });

// 휴대폰에서 올리기 (토큰 필요). multipart: file(여러 개), from(이름, 선택)
app.MapPost("/api/up", async (HttpContext ctx) =>
{
    if (!inbox.CheckToken(ctx.Request.Query["t"])) return Results.Json(new { ok = false, error = "QR 코드를 다시 찍어 주세요" }, statusCode: 403);
    if (!ctx.Request.HasFormContentType) return Results.Json(new { ok = false, error = "사진이 없어요" }, statusCode: 400);
    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    var from = form["from"].ToString();
    int ok = 0;
    var errors = new List<string>();
    foreach (var f in form.Files)
    {
        if (f.Length > PhotoInbox.MaxFileBytes) { errors.Add($"{f.FileName}: 사진이 너무 커요 (15MB까지)"); continue; }
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms, ctx.RequestAborted);
        var err = inbox.Add(Path.GetFileNameWithoutExtension(f.FileName), from, f.ContentType, ms.ToArray());
        if (err == null) ok++; else errors.Add($"{f.FileName}: {err}");
    }
    if (ok > 0) Console.WriteLine($"  휴대폰 사진 {ok}장 도착{(string.IsNullOrWhiteSpace(from) ? "" : $" ({from})")}");
    return Results.Json(new { ok = ok > 0, count = ok, errors }, statusCode: ok > 0 ? 200 : 400);
});

// 스켈레톤 소스 시작 (별도 스레드)
using var cts = new CancellationTokenSource();
ISkeletonSource source = options.Demo
    ? new DemoSkeletonSource(hub)
    : new FemtoBoltSkeletonSource(hub, options.ProcessingMode);
var sourceThread = new Thread(() => source.Run(cts.Token)) { IsBackground = true, Name = "SkeletonSource" };
sourceThread.Start();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://localhost:{options.Port}";
    Console.WriteLine();
    Console.WriteLine("  Femto Bolt Skeleton Agent");
    Console.WriteLine($"  모드    : {(options.Demo ? "데모 (가짜 스켈레톤)" : $"카메라 (처리: {options.ProcessingModeName})")}");
    Console.WriteLine($"  웹 주소 : {url}");
    Console.WriteLine($"  WS 주소 : ws://localhost:{options.Port}/ws");
    Console.WriteLine($"  웹 파일 : {webRoot}");
    if (options.Lan)
        Console.WriteLine($"  사진받기: {inbox.UploadUrl(options.Port) ?? "(네트워크 주소를 찾지 못함 - 와이파이/랜 연결 확인)"}");
    Console.WriteLine("  종료    : Ctrl+C");
    Console.WriteLine();
    if (options.OpenBrowser)
        BrowserLauncher.Open(options.StartViewer ? url + "/" : url + "/games/", fullscreen: !options.Windowed);
});

app.Lifetime.ApplicationStopping.Register(() => { cts.Cancel(); registry.Save(); });
app.Run();
sourceThread.Join(TimeSpan.FromSeconds(3));


namespace FemtoSkeletonAgent
{
    public sealed class AgentOptions
    {
        public bool Demo { get; private set; }
        public int Port { get; private set; } = 5000;
        public bool OpenBrowser { get; private set; } = true;
        public bool StartViewer { get; private set; }
        public bool Windowed { get; private set; }
        public bool Lan { get; private set; } = true;
        public string ProcessingModeName { get; private set; } = "gpu";

        // gpu: Windows에서는 SDK가 DirectML(대부분의 GPU에서 동작, CUDA 설치 불필요)을 자동 선택
        // cpu: GPU가 없을 때. 느립니다 (대략 수 fps)
        public Microsoft.Azure.Kinect.BodyTracking.TrackerProcessingMode ProcessingMode => ProcessingModeName switch
        {
            "cpu" => Microsoft.Azure.Kinect.BodyTracking.TrackerProcessingMode.Cpu,
            _ => Microsoft.Azure.Kinect.BodyTracking.TrackerProcessingMode.Gpu,
        };

        public static AgentOptions Parse(string[] args)
        {
            var o = new AgentOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--demo": o.Demo = true; break;
                    case "--no-browser": o.OpenBrowser = false; break;
                    case "--viewer": o.StartViewer = true; break;
                    case "--window": o.Windowed = true; break;
                    case "--no-lan": o.Lan = false; break;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): o.Port = p; i++; break;
                    case "--mode" when i + 1 < args.Length: o.ProcessingModeName = args[++i].ToLowerInvariant(); break;
                }
            }
            return o;
        }
    }
}
