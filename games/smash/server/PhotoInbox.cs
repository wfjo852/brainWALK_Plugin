using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using QRCoder;

namespace FemtoSkeletonAgent.Games.Smash;

/// <summary>
/// 휴대폰 사진 받기 (사진 깨기 게임용)
///
///  - 게임 화면에 QR 코드 → 휴대폰으로 찍으면 http://(이 PC 주소):포트/games/smash/up/?t=(토큰) 페이지가 열림
///  - 휴대폰이 사진을 보내면 여기(메모리)에 잠시 보관 → 게임 화면이 가져가서 브라우저 저장소에 넣고 지움
///  - 토큰: 실행할 때마다 새로 만듦. QR을 본 사람만 올릴 수 있게
///  - 사진은 인터넷으로 나가지 않음 (같은 와이파이 안에서만)
/// </summary>
public sealed class PhotoInbox
{
    public const long MaxFileBytes = 15 * 1024 * 1024;
    const int MaxPending = 60;
    static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "image/jpeg", ["image/jpg"] = "image/jpeg", ["image/png"] = "image/png",
        ["image/webp"] = "image/webp", ["image/gif"] = "image/gif",
        ["image/heic"] = "image/heic", ["image/heif"] = "image/heif",
    };

    public sealed record Item(int Seq, string Name, string From, string ContentType, byte[] Data, DateTime At);

    readonly object gate = new();
    readonly List<Item> pending = new();
    int seq;

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    public bool CheckToken(string? t) =>
        t != null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(t), System.Text.Encoding.ASCII.GetBytes(Token));

    /// <summary>받은 사진 넣기. 실패하면 이유(한글)를 돌려줌</summary>
    public string? Add(string name, string from, string? contentType, byte[] data)
    {
        if (data.Length == 0) return "빈 파일이에요";
        if (data.Length > MaxFileBytes) return "사진이 너무 커요 (15MB까지)";
        if (contentType == null || !Allowed.TryGetValue(contentType, out var type)) return "사진 파일이 아니에요";
        lock (gate)
        {
            if (pending.Count >= MaxPending) return "사진이 너무 많이 쌓였어요. 잠시 후 다시 보내 주세요";
            pending.Add(new Item(++seq, Clean(name, 40), Clean(from, 12), type, data, DateTime.Now));
        }
        return null;
    }

    public object[] List()
    {
        lock (gate) return pending.Select(p => (object)new { seq = p.Seq, name = p.Name, from = p.From, type = p.ContentType }).ToArray();
    }

    public Item? Get(int s) { lock (gate) return pending.FirstOrDefault(p => p.Seq == s); }

    public void Remove(int s) { lock (gate) pending.RemoveAll(p => p.Seq == s); }

    static string Clean(string? s, int max)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    // ───────── 이 PC의 와이파이/랜 주소 ─────────

    /// <summary>휴대폰이 접속할 이 PC의 IPv4 주소 (게이트웨이가 있는 실제 네트워크 우선)</summary>
    public static string? LanAddress()
    {
        try
        {
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n =>
                {
                    var props = n.GetIPProperties();
                    var ip = props.UnicastAddresses.Select(a => a.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                                             && !a.ToString().StartsWith("169.254."));
                    var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    var desc = (n.Description + " " + n.Name).ToLowerInvariant();
                    var isVirtual = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker");
                    return new { ip, score = (hasGateway ? 2 : 0) + (isVirtual ? 0 : 1) };
                })
                .Where(x => x.ip != null)
                .OrderByDescending(x => x.score)
                .FirstOrDefault();
            return best?.ip?.ToString();
        }
        catch { return null; }
    }

    public string? UploadUrl(int port)
    {
        var ip = LanAddress();
        return ip == null ? null : $"http://{ip}:{port}{SmashModule.PhonePage}?t={Token}";
    }

    public static string QrSvg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(10, "#000000", "#ffffff", drawQuietZones: true);
    }
}
