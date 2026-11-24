namespace FemtoSkeletonAgent;

/// <summary>
/// 웹 화면 파일 위치 찾기 + 변경 감시.
///   뷰어: agent\wwwroot     게임: (저장소 맨 위)\games
/// 실행 파일은 agent\bin\...\ 아래에 있지만, 원본 폴더를 직접 쓰면
/// 파일을 고칠 때마다 빌드할 필요가 없습니다. 원본을 못 찾으면 실행 폴더의 사본(wwwroot, games)을 씁니다.
/// </summary>
public static class WebRootLocator
{
    /// <summary>게임 폴더: 원본(agent 옆의 games) → 없으면 실행 폴더의 games</summary>
    public static string FindGames()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FemtoSkeletonAgent.csproj")) && dir.Parent != null)
            {
                var candidate = Path.Combine(dir.Parent.FullName, "games");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
                break;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "games");
    }

    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(dir.FullName, "FemtoSkeletonAgent.csproj")) &&
                File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    /// <summary>폴더 안 파일이 바뀌면 onChange 호출 (편집기가 여러 번 저장해도 0.4초에 한 번만)</summary>
    public static IDisposable Watch(string root, Action onChange)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        Timer? timer = null;
        var gate = new object();
        void Changed(object? _, FileSystemEventArgs e)
        {
            // 편집기 임시 파일은 무시
            var name = Path.GetFileName(e.FullPath);
            if (name.StartsWith('.') || name.StartsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                timer?.Dispose();
                timer = new Timer(_ => onChange(), null, 400, Timeout.Infinite);
            }
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
