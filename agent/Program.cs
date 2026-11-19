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
//    --no-lan          다른 기기 접속 끄기 (휴대폰 사진 올리기 등) → 이 PC에서만 접속
//    --games <폴더>    게임 폴더 위치 지정 (기본: 원본 games 폴더, 없으면 실행 폴더의 games)
// ─────────────────────────────────────────────────────────────

var options = AgentOptions.Parse(args);
// 등록된 사람 목록 (데모와 실제 카메라는 따로 저장)
var registry = new PersonRegistry(PersonRegistry.DefaultPath(options.Demo));
var hub = new SkeletonHub(registry);

// 웹 화면 파일은 원본 폴더를 바로 사용합니다.
//   agent\wwwroot → /        뷰어 (스켈레톤 화면)
//   games\        → /games/  게임 (에이전트와 따로 배포: 이 폴더만 바꾸면 됨)
// → 고치면 다시 빌드할 필요 없이 열린 브라우저가 자동으로 새로고침됩니다.
var webRoot = WebRootLocator.Find();
var gamesRoot = options.GamesDir != null ? Path.GetFullPath(options.GamesDir) : WebRootLocator.FindGames();
var gameCtx = new GameContext(options.Port, options.Lan, gamesRoot);
var gameModules = GameModules.Discover();   // games\<게임>\server\*.cs 에 있는 게임별 서버 기능

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot,
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// 같은 와이파이의 휴대폰 등이 접속할 수 있게 모든 네트워크에서 받음.
// 단, 다른 기기는 게임 모듈이 허용한 주소만 쓸 수 있음 (아래 문지기). --no-lan 이면 이 PC에서만 접속
builder.WebHost.UseUrls(options.Lan ? $"http://*:{options.Port}" : $"http://localhost:{options.Port}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 40 * 1024 * 1024);

var app = builder.Build();

// 문지기: 이 PC가 아닌 곳(휴대폰)에서 온 요청은 게임 모듈이 허용한 주소만
var remoteRules = gameModules.SelectMany(m => m.RemoteAccess).ToList();
app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip == null || System.Net.IPAddress.IsLoopback(ip)) { await next(); return; }
    var path = ctx.Request.Path.Value ?? "";
    if (!remoteRules.Any(r => r.Matches(ctx.Request.Method, path))) { ctx.Response.StatusCode = 404; return; }
    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
// 브라우저가 옛 파일을 들고 있지 않도록 항상 새로 확인
static void NoCache(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext c) => c.Context.Response.Headers.CacheControl = "no-cache";
// 게임: /games/ ← games 폴더
if (Directory.Exists(gamesRoot))
{
    var gamesFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(gamesRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = gamesFiles, RequestPath = "/games" });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = gamesFiles, RequestPath = "/games", OnPrepareResponse = NoCache });
}
// 뷰어: / ← agent\wwwroot
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = NoCache });

// 웹 화면 파일이 바뀌면 열린 브라우저에 "새로고침" 신호
void Reload()
{
    Console.WriteLine("  웹 화면 파일 변경 감지 → 브라우저 새로고침");
    hub.PublishReload();
}
using var webWatcher = WebRootLocator.Watch(webRoot, Reload);
using var gamesWatcher = Directory.Exists(gamesRoot) ? WebRootLocator.Watch(gamesRoot, Reload) : null;

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

// 게임 종료 (게임 고르기 화면의 "게임 종료" 버튼): 게임 창을 닫고 에이전트도 끔
//  - 이 PC에서만 (문지기), 게임 화면에서 보낸 요청만 (X-Femto 머리글: 다른 웹사이트가 몰래 끄지 못하게)
app.MapPost("/api/quit", (HttpContext ctx, IHostApplicationLifetime life) =>
{
    if (ctx.Request.Headers["X-Femto"] != "quit") return Results.StatusCode(403);
    Console.WriteLine("  게임 화면에서 종료 요청 → 게임 창을 닫고 종료합니다");
    _ = Task.Run(async () =>
    {
        await Task.Delay(300);   // 응답을 먼저 보내고
        BrowserLauncher.CloseAppWindows();
        life.StopApplication();
    });
    return Results.Ok();
});

// 게임별 서버 기능: /api/<게임>/...
foreach (var m in gameModules)
    m.Map(app.MapGroup($"/api/{m.Name}"), gameCtx);

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
    Console.WriteLine($"  뷰어 파일: {webRoot}");
    Console.WriteLine($"  게임 파일: {(Directory.Exists(gamesRoot) ? gamesRoot : gamesRoot + "  (없음! 게임 화면이 열리지 않습니다)")}");
    if (gameModules.Count > 0)
        Console.WriteLine($"  게임 서버: {string.Join(", ", gameModules.Select(m => m.Name))}");
    foreach (var m in gameModules)
        if (m.StartupInfo(gameCtx) is { } line) Console.WriteLine($"  [{m.Name}] {line}");
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
        public string? GamesDir { get; private set; }
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
                    case "--games" when i + 1 < args.Length: o.GamesDir = args[++i]; break;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): o.Port = p; i++; break;
                    case "--mode" when i + 1 < args.Length: o.ProcessingModeName = args[++i].ToLowerInvariant(); break;
                }
            }
            return o;
        }
    }
}
