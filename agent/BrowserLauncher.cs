using System.Diagnostics;

namespace FemtoSkeletonAgent;

/// <summary>
/// 게임 화면을 "앱 창"으로 열기.
/// 브라우저는 사람이 화면을 직접 누르기 전에는 음성·효과음을 막는데, 어르신들은 손으로만 조작하므로
/// 소리를 허용하는 설정(--autoplay-policy=no-user-gesture-required)으로 Edge(또는 Chrome)를 따로 띄웁니다.
///  - 평소 쓰는 브라우저와 섞이지 않도록 별도 프로필(%LocalAppData%\FemtoSkeleton\browser) 사용
///  - 주소창·탭 없는 전체 화면 (F11 = 창 모드, Alt+F4 = 닫기)
/// Edge·Chrome을 못 찾으면 기본 브라우저로 엽니다 (이때는 화면을 한 번 눌러야 소리가 날 수 있음).
/// </summary>
public static class BrowserLauncher
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>게임 창 전용 브라우저 프로필 (평소 브라우저와 분리)</summary>
    public static string ProfileDir => Path.Combine(Local, "FemtoSkeleton", "browser");

    /// <summary>
    /// 게임 창 닫기 (게임 종료 버튼): 전용 프로필로 뜬 Edge/Chrome만 닫음. 평소 쓰는 브라우저는 건드리지 않음.
    /// 먼저 정상 종료를 요청하고, 3초 뒤에도 남아 있으면 강제로 닫음.
    /// </summary>
    public static void CloseAppWindows()
    {
        var profile = ProfileDir.Replace("'", "''");
        var script =
            "$p = { Get-CimInstance Win32_Process -Filter \"Name='msedge.exe' or Name='chrome.exe'\" | " +
            $"Where-Object {{ $_.CommandLine -like '*{profile}*' }} }}; " +
            "& $p | Where-Object { $_.CommandLine -notlike '*--type=*' } | ForEach-Object { taskkill /PID $_.ProcessId /T 2>$null | Out-Null }; " +
            "Start-Sleep -Seconds 3; " +
            "& $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
        try
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            { UseShellExecute = false, CreateNoWindow = true });
            ps?.WaitForExit(10000);
        }
        catch (Exception ex) { Console.WriteLine($"  게임 창을 닫지 못했습니다: {ex.Message}"); }
    }

    public static void Open(string url, bool fullscreen)
    {
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string local = Local;
        string[] candidates =
        {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe != null)
        {
            var profile = ProfileDir;
            var args = $"--app=\"{url}\" --user-data-dir=\"{profile}\" --autoplay-policy=no-user-gesture-required " +
                       "--no-first-run --no-default-browser-check --disable-features=Translate" +
                       (fullscreen ? " --start-fullscreen" : " --window-size=1600,900");
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                Console.WriteLine($"  화면 열기: {Path.GetFileNameWithoutExtension(exe)} 앱 창 ({(fullscreen ? "전체 화면, F11 = 창 모드" : "창")}, Alt+F4 = 닫기)");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  앱 창을 열지 못해 기본 브라우저로 엽니다: {ex.Message}");
            }
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* 무시 */ }
        Console.WriteLine("  기본 브라우저로 열었습니다. 소리가 안 나면 화면을 한 번 눌러 주세요.");
    }
}
