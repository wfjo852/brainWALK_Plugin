namespace FemtoSkeletonAgent.Games.Smash;

/// <summary>
/// 사진 깨기 서버 기능: 휴대폰 사진 받기 (QR)
///
///  게임 화면용 (이 PC에서만)
///    GET    /api/smash/up/info     → { url, lan }  QR에 넣을 주소
///    GET    /api/smash/up/qr.svg   → QR 그림
///    GET    /api/smash/up/list     → 도착한 사진 목록
///    GET    /api/smash/up/{seq}    → 사진 파일
///    DELETE /api/smash/up/{seq}    → 가져간 사진 지우기
///  휴대폰용 (같은 와이파이의 다른 기기도 허용)
///    GET    /games/smash/up/       → 사진 보내기 페이지
///    POST   /api/smash/up?t=토큰   → 사진 올리기 (multipart: file 여러 개, from 이름)
/// </summary>
public sealed class SmashModule : IGameModule
{
    public const string PhonePage = "/games/smash/up/";
    const string UploadApi = "/api/smash/up";

    readonly PhotoInbox inbox = new();

    public string Name => "smash";

    public IEnumerable<RemoteAccess> RemoteAccess =>
    [
        new("GET", PhonePage, Prefix: true),
        new("POST", UploadApi),
    ];

    public string? StartupInfo(GameContext ctx) =>
        ctx.Lan ? $"사진받기: {inbox.UploadUrl(ctx.Port) ?? "(네트워크 주소를 찾지 못함 - 와이파이/랜 연결 확인)"}" : null;

    public void Map(RouteGroupBuilder api, GameContext ctx)
    {
        api.MapGet("/up/info", () => Results.Json(new { url = ctx.Lan ? inbox.UploadUrl(ctx.Port) : null, lan = ctx.Lan }));
        api.MapGet("/up/qr.svg", () =>
        {
            var url = ctx.Lan ? inbox.UploadUrl(ctx.Port) : null;
            return url == null ? Results.NotFound() : Results.Text(PhotoInbox.QrSvg(url), "image/svg+xml");
        });
        api.MapGet("/up/list", () => Results.Json(inbox.List()));
        api.MapGet("/up/{seq:int}", (int seq, HttpContext http) =>
        {
            var item = inbox.Get(seq);
            if (item == null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(item.Data, item.ContentType);
        });
        api.MapDelete("/up/{seq:int}", (int seq) => { inbox.Remove(seq); return Results.Ok(); });

        api.MapPost("/up", async (HttpContext http) =>
        {
            if (!inbox.CheckToken(http.Request.Query["t"])) return Results.Json(new { ok = false, error = "QR 코드를 다시 찍어 주세요" }, statusCode: 403);
            if (!http.Request.HasFormContentType) return Results.Json(new { ok = false, error = "사진이 없어요" }, statusCode: 400);
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var from = form["from"].ToString();
            int ok = 0;
            var errors = new List<string>();
            foreach (var f in form.Files)
            {
                if (f.Length > PhotoInbox.MaxFileBytes) { errors.Add($"{f.FileName}: 사진이 너무 커요 (15MB까지)"); continue; }
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, http.RequestAborted);
                var err = inbox.Add(Path.GetFileNameWithoutExtension(f.FileName), from, f.ContentType, ms.ToArray());
                if (err == null) ok++; else errors.Add($"{f.FileName}: {err}");
            }
            if (ok > 0) Console.WriteLine($"  휴대폰 사진 {ok}장 도착{(string.IsNullOrWhiteSpace(from) ? "" : $" ({from})")}");
            return Results.Json(new { ok = ok > 0, count = ok, errors }, statusCode: ok > 0 ? 200 : 400);
        });
    }
}
