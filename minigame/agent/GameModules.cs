using System.Reflection;

namespace FemtoSkeletonAgent;

/// <summary>
/// 게임별 서버 기능을 붙이는 틀.
///
///  - 웹 게임은 games\&lt;게임&gt;\ 폴더의 HTML/JS만으로 동작하는 게 기본입니다 (에이전트는 /games/ 로 파일만 제공).
///  - 서버 기능이 꼭 필요한 게임만 games\&lt;게임&gt;\server\*.cs 에 IGameModule 을 만들면,
///    빌드할 때 함께 컴파일되고 에이전트가 시작할 때 자동으로 붙습니다. (에이전트 코드는 게임 이름을 모름)
///  - API 주소는 /api/&lt;게임 이름&gt;/... 로 묶입니다.
/// </summary>
public interface IGameModule
{
    /// <summary>게임 폴더 이름 (예: "smash"). API 주소 /api/smash/... 에 쓰임</summary>
    string Name { get; }

    /// <summary>이 PC가 아닌 기기(휴대폰 등)도 쓸 수 있는 주소. 나머지는 모두 이 PC에서만</summary>
    IEnumerable<RemoteAccess> RemoteAccess => [];

    /// <summary>API 등록. api = /api/&lt;Name&gt; 로 묶인 그룹</summary>
    void Map(RouteGroupBuilder api, GameContext ctx);

    /// <summary>에이전트 시작 후 콘솔에 보여 줄 한 줄 (없으면 null)</summary>
    string? StartupInfo(GameContext ctx) => null;
}

/// <summary>다른 기기 허용 규칙: Method(GET/POST) + 주소(PathPrefix로 시작하면 허용, Exact면 정확히 같을 때만)</summary>
public sealed record RemoteAccess(string Method, string Path, bool Prefix = false)
{
    public bool Matches(string method, string path) =>
        string.Equals(method, Method, StringComparison.OrdinalIgnoreCase)
        && (Prefix ? path.StartsWith(Path, StringComparison.OrdinalIgnoreCase) || path.Equals(Path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                   : path.Equals(Path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>게임 모듈이 쓸 수 있는 에이전트 정보</summary>
public sealed record GameContext(int Port, bool Lan, string GamesRoot);

public static class GameModules
{
    /// <summary>이 프로그램에 함께 빌드된 게임 모듈을 모두 찾음</summary>
    public static IReadOnlyList<IGameModule> Discover() =>
        Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IGameModule).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (IGameModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Name)
            .ToList();
}
