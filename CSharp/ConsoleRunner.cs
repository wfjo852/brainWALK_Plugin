using System.Runtime.InteropServices;
using System.Text;

namespace BrainWalk;

internal static class ConsoleRunner
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static async Task<int> RunAsync()
    {
        OpenConsole();
        Console.Title = "BrainWalk Console";
        Console.WriteLine("BrainWalk console mode");
        Console.WriteLine("저장된 설정으로 LiDAR와 ANT를 시작합니다. 종료: Ctrl+C");

        var config = new AppConfig();
        var lidar = new LidarService();
        var ant = new AntService();
        using var stopped = new ManualResetEventSlim();

        lidar.Status += message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        ant.Status += message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        ant.BpmReceived += bpm => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] BPM {bpm}");

        ConfigureLidar(lidar, config);
        ant.SelectedDevice = new AntDevice(
            config.Get("ant_device", "device_id", 0),
            config.Get("ant_device", "device_type", 120),
            config.Get("ant_device", "transmission_type", 0));

        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            stopped.Set();
        };
        Console.CancelKeyPress += cancel;

        try
        {
            string port = config.Get("connection", "port", "COM4");
            int baudRate = config.Get("connection", "baudrate", 230400);
            double frequency = Math.Clamp(config.Get("connection", "scan_frequency", 7.0), 5.4, 11.9);
            Console.WriteLine($"LiDAR: {port}, {baudRate} bps, {frequency:F1} Hz");
            Console.WriteLine($"마우스 제어: {(lidar.MouseEnabled ? "사용" : "사용 안 함")}, 드래그: {(lidar.DragEnabled ? "사용" : "사용 안 함")}");
            Console.WriteLine($"ANT: {ant.SelectedDevice}");

            ant.Start();
            await lidar.StartAsync(port, baudRate, frequency);
            await Task.Run(stopped.Wait);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"시작 오류: {ex.GetBaseException().Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            lidar.Dispose();
            ant.Dispose();
            Console.WriteLine("BrainWalk가 종료되었습니다.");
        }
    }

    private static void ConfigureLidar(LidarService lidar, AppConfig config)
    {
        lidar.Threshold = config.Get("touch_detection", "threshold_mm", 1000);
        lidar.MinPoints = config.Get("touch_detection", "min_points", 1);
        lidar.AreaX = config.Get("touch_area", "x", -300);
        lidar.AreaY = config.Get("touch_area", "y", 330);
        lidar.AreaWidth = config.Get("touch_area", "width", 600);
        lidar.AreaHeight = config.Get("touch_area", "height", -300);
        lidar.OffsetX = config.Get("touch_offset", "x_mm", 0.0);
        lidar.OffsetY = config.Get("touch_offset", "y_mm", 0.0);
        lidar.StartAngle = config.Get("lidar_position", "scan_start_angle", 90);
        lidar.EndAngle = config.Get("lidar_position", "scan_end_angle", 270);
        lidar.Rotation = config.Get("lidar_position", "rotation", 90);
        lidar.FlipX = config.Get("lidar_position", "flip_x", true);
        lidar.FlipY = config.Get("lidar_position", "flip_y", false);
        // Console mode is the unattended touch mode, so mouse control starts immediately.
        lidar.MouseEnabled = true;
        lidar.DragEnabled = config.Get("mouse_control", "drag_enabled", true);

        int monitorIndex = config.Get("mouse_control", "monitor_index", 0);
        var screens = Screen.AllScreens;
        if (screens.Length > 0)
            lidar.TargetScreenBounds = screens[Math.Clamp(monitorIndex, 0, screens.Length - 1)].Bounds;
    }

    private static void OpenConsole()
    {
        if (!AttachConsole(AttachParentProcess)) AllocConsole();
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}
