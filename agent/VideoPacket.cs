using System.Buffers.Binary;
using System.IO.Compression;

namespace FemtoSkeletonAgent;

/// <summary>
/// 브라우저로 보내는 영상 1프레임 (WebSocket 바이너리 메시지).
///
///  [0]      'V' (0x56)
///  [1]      이미지 형식: 1 = JPEG, 2 = BMP
///  [2..3]   이미지 너비   (uint16, little endian)
///  [4..5]   이미지 높이
///  [6..7]   마스크 너비
///  [8..9]   마스크 높이
///  [10..13] 이미지 바이트 수 (uint32)
///  [14..17] 마스크 바이트 수 (uint32, zlib 압축)
///  [18..]   이미지 바이트, 이어서 마스크 바이트
///
/// 마스크: 픽셀당 1바이트. 사람이 차지하는 비율 0(배경) ~ 255(사람).
/// 관절의 2D 좌표("p2")는 이미지 픽셀 기준이며 JSON 프레임에 들어 있습니다.
/// </summary>
public static class VideoPacket
{
    public const byte FormatJpeg = 1;
    public const byte FormatBmp = 2;
    private const int HeaderSize = 18;

    public static byte[] Build(byte format, int imageW, int imageH, ReadOnlySpan<byte> image, int maskW, int maskH, ReadOnlySpan<byte> mask)
    {
        var maskZ = Compress(mask);
        var buf = new byte[HeaderSize + image.Length + maskZ.Length];
        buf[0] = (byte)'V';
        buf[1] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)imageW);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)imageH);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), (ushort)maskW);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)maskH);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(10), (uint)image.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(14), (uint)maskZ.Length);
        image.CopyTo(buf.AsSpan(HeaderSize));
        maskZ.CopyTo(buf.AsSpan(HeaderSize + image.Length));
        return buf;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.Length / 8 + 64);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// 컬러 해상도의 body index map(배경 255)을 factor 배로 줄이면서
    /// 각 칸에서 사람이 차지하는 비율(0~255)을 마스크로 만듭니다.
    /// 0/1로 자르지 않고 비율을 남겨야 브라우저에서 경계를 매끄럽게 다듬을 수 있습니다.
    /// </summary>
    public static byte[] DownsampleBodyIndex(ReadOnlySpan<byte> src, int width, int height, int stride, int factor, out int outW, out int outH)
    {
        outW = width / factor;
        outH = height / factor;
        var dst = new byte[outW * outH];
        int area = factor * factor;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int count = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    var row = src.Slice((y * factor + dy) * stride + x * factor, factor);
                    foreach (var b in row)
                        if (b != 255) count++;
                }
                dst[y * outW + x] = (byte)(count * 255 / area);
            }
        }
        return dst;
    }
}
