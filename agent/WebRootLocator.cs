namespace FemtoSkeletonAgent;

/// <summary>
/// 웹 화면 파일(wwwroot) 위치 찾기 + 변경 감시.
/// 실행 파일은 agent\bin\...\ 아래에 있지만, 원본 agent\wwwroot 를 직접 쓰면
/// 파일을 고칠 때마다 빌드할 필요가 없습니다. 원본을 못 찾으면 실행 폴더의 사본을 씁니다.
/// </summary>
public static class WebRootLocator
{
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
