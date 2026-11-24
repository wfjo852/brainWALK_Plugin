using System.Runtime.InteropServices;

namespace BrainWalk;

// 게임 실행 전에 필요한 장치가 USB에 연결되어 있는지 확인한다.
internal static class DeviceCheck
{
    // Orbbec(Femto Bolt/Mega 등)의 USB Vendor ID.
    public const string DefaultFemtoVendorId = "2BC5";

    public static bool IsUsbVendorPresent(string vendorId)
    {
        string token = $"VID_{vendorId.Trim()}";
        return GetPresentUsbDeviceIds().Any(id => id.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetPresentUsbDeviceIds()
    {
        const int FilterEnumerator = 0x1, FilterPresent = 0x100, Success = 0, BufferSmall = 0x1A;
        const int flags = FilterEnumerator | FilterPresent;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out int length, "USB", flags) != Success) return [];
            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW("USB", buffer, length, flags);
            if (result == BufferSmall) continue; // 조회 사이에 장치가 추가된 경우 다시 시도
            if (result != Success) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int length, int flags);
}

// 콘솔 플러그인이 LiDAR/ANT 시작 결과를 런처에 알려주는 파일.
// 각 줄은 "lidar=<오류 메시지>" 형식이며, 값이 비어 있으면 정상 연결이다.
internal sealed record PluginStartResult(string? LidarError, string? AntError)
{
    public static string FilePath => Path.Combine(Path.GetTempPath(), "brainWALK_plugin_status.txt");

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }

    public void Write()
    {
        string temp = FilePath + ".tmp";
        File.WriteAllLines(temp, [$"lidar={Clean(LidarError)}", $"ant={Clean(AntError)}"]);
        File.Move(temp, FilePath, overwrite: true);
    }

    public static PluginStartResult? TryRead()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var values = File.ReadAllLines(FilePath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            return new PluginStartResult(Value(values, "lidar"), Value(values, "ant"));
        }
        catch { return null; }
    }

    private static string? Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static string Clean(string? message) =>
        message?.Replace('\r', ' ').Replace('\n', ' ') ?? "";
}
