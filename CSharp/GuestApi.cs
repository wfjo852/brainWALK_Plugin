using System.Net;
using System.Text.RegularExpressions;

namespace BrainWalk;

// memorywalk 서버의 게스트 생성.
// 별도 API 대신 화면용 폼 엔드포인트(POST /villages/guests/new)를 그대로 호출한다.
// 이 엔드포인트는 JWT 인증·CSRF 검사 예외라 로그인 없이 동작한다.
//   성공: 301 + Location: /villages/guests/{guestid}/created
//   실패: 400 + HTML 오류 페이지
// 얻은 guestid는 게임에서 POST /api/villages/auth/guest/login 에 넣어 로그인하는 값이다.
internal static partial class GuestApi
{
    public const string DefaultServerUrl = "https://raincoat77.memorywalk.kr/";

    // 리디렉션을 따라가면 Location 헤더를 읽을 수 없으므로 자동 리디렉션을 끈다.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    [GeneratedRegex(@"/villages/guests/([^/?#]+)/created", RegexOptions.IgnoreCase)]
    private static partial Regex CreatedLocation();

    // 게스트를 만들고 로그인에 쓸 guestid를 반환한다.
    public static async Task<string> CreateGuestAsync(string serverUrl, string name, DateTime birthday, int restingHr)
    {
        var endpoint = new Uri(new Uri(NormalizeBase(serverUrl)), "villages/guests/new");
        // FormUrlEncodedContent는 한글을 UTF-8로 퍼센트 인코딩한다.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = name,
            ["birthday"] = birthday.ToString("yyyy-MM-dd"),
            ["resting_hr"] = restingHr.ToString()
        });

        using var response = await Http.PostAsync(endpoint, form);
        string location = response.Headers.Location?.OriginalString ?? "";
        var match = CreatedLocation().Match(location);
        if (IsRedirect(response.StatusCode) && match.Success)
            return Uri.UnescapeDataString(match.Groups[1].Value);

        string reason = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "입력값이 올바르지 않습니다. 이름·생년월일·안정시 심박수를 확인해주세요.",
            _ when IsRedirect(response.StatusCode) => $"예상하지 못한 이동 주소입니다: {location}",
            _ => "서버에서 게스트를 만들지 못했습니다."
        };
        throw new InvalidOperationException($"{reason} ({(int)response.StatusCode} {response.ReasonPhrase})");
    }

    // 게스트 결과 페이지: {서버 URL}/villages/guests/{guestid}
    public static Uri ResultPageUrl(string serverUrl, string guestId) =>
        new(new Uri(NormalizeBase(serverUrl)), $"villages/guests/{Uri.EscapeDataString(guestId)}");

    private static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and < 400;

    private static string NormalizeBase(string url)
    {
        url = string.IsNullOrWhiteSpace(url) ? DefaultServerUrl : url.Trim();
        return url.EndsWith('/') ? url : url + "/";
    }
}
