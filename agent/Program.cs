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
// 1단계: 같은 PC에서만 접속 (localhost 전용)
builder.WebHost.UseUrls($"http://localhost:{options.Port}");

var app = builder.Build();
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
                    case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): o.Port = p; i++; break;
                    case "--mode" when i + 1 < args.Length: o.ProcessingModeName = args[++i].ToLowerInvariant(); break;
                }
            }
            return o;
        }
    }
}
