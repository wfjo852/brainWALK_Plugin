namespace FemtoSkeletonAgent;

/// <summary>
/// 데모 모드용 가짜 컬러 영상.
/// 스켈레톤을 핀홀 카메라로 투영해서 "사람 모양"을 칠하고, 뒤에는 방 배경을 그립니다.
/// 실제 카메라처럼 이미지 + 사람 마스크 + 관절 2D 좌표를 만들어 배경 제거 화면을 시험할 수 있습니다.
/// 서버에 JPEG 인코더가 없으므로 데모는 BMP(무압축)로 보냅니다 (같은 PC 안이라 충분히 빠름).
/// </summary>
public sealed class DemoVideo
{
    public const int W = 640, H = 360;
    private const float Fx = 380f, Fy = 380f, Cx = W / 2f, Cy = H / 2f; // 720p 기준 fx≈760 의 절반 해상도

    private readonly byte[] _rgb = new byte[W * H * 3];
    private readonly byte[] _mask = new byte[W * H];
    private readonly byte[] _rough = new byte[W * H];
    private readonly Random _rng = new(11);
    private readonly byte[] _background = new byte[W * H * 3];

    // 사람별 옷 색 (상의, 하의), 피부
    private static readonly (byte r, byte g, byte b)[] Shirts = { (214, 72, 64), (64, 140, 214), (240, 190, 70), (120, 190, 110) };
    private static readonly (byte r, byte g, byte b)[] Pants = { (40, 52, 84), (70, 70, 74), (96, 64, 48), (50, 80, 60) };
    private static readonly (byte r, byte g, byte b) Skin = (232, 190, 160);
    private static readonly (byte r, byte g, byte b) Hair = (40, 30, 26);

    public DemoVideo()
    {
        // 방 배경: 벽(위) + 바닥(아래) + 세로 줄무늬 — 마스크로 지워져야 하는 부분
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                bool floor = y > H * 0.62;
                float t = y / (float)H;
                byte r = floor ? (byte)(120 - 30 * t) : (byte)(190 - 40 * t);
                byte g = floor ? (byte)(96 - 20 * t) : (byte)(176 - 40 * t);
                byte b = floor ? (byte)(70 - 10 * t) : (byte)(150 - 30 * t);
                if (!floor && (x / 40) % 2 == 0) { r -= 12; g -= 12; b -= 12; }
                _background[i] = r; _background[i + 1] = g; _background[i + 2] = b;
            }
    }

    /// <summary>3D 관절(mm, 카메라 좌표) → 이미지 픽셀</summary>
    public static float[] Project(float[] joints)
    {
        var p2 = new float[SkeletonHub.JointCount * 2];
        for (int j = 0; j < SkeletonHub.JointCount; j++)
        {
            float x = joints[j * 4], y = joints[j * 4 + 1], z = joints[j * 4 + 2];
            p2[j * 2] = z > 1 ? Fx * x / z + Cx : -1;
            p2[j * 2 + 1] = z > 1 ? Fy * y / z + Cy : -1;
        }
        return p2;
    }

    public byte[] Render(IReadOnlyList<BodyData> bodies)
    {
        Array.Copy(_background, _rgb, _rgb.Length);
        Array.Clear(_mask);

        // 먼 사람부터 칠하기
        var order = Enumerable.Range(0, bodies.Count).OrderByDescending(i => bodies[i].Joints[2]).ToList();
        foreach (var bi in order)
        {
            var b = bodies[bi];
            var p = b.Points2D ?? Project(b.Joints);
            float z = MathF.Max(500, b.Joints[2]);              // 골반 깊이로 굵기 결정
            float px(float meters) => Fx * meters * 1000f / z;  // 실제 크기(m) → 픽셀
            var shirt = Shirts[bi % Shirts.Length];
            var pants = Pants[bi % Pants.Length];
            byte m = (byte)(bi + 1);

            void Seg(int a, int c, float radiusM, (byte, byte, byte) col) =>
                Capsule(p[a * 2], p[a * 2 + 1], p[c * 2], p[c * 2 + 1], px(radiusM), col, m);

            // 다리 (바지)
            Seg(22, 23, 0.075f, pants); Seg(23, 24, 0.055f, pants); Seg(24, 25, 0.045f, (30, 30, 34));
            Seg(18, 19, 0.075f, pants); Seg(19, 20, 0.055f, pants); Seg(20, 21, 0.045f, (30, 30, 34));
            // 몸통 (상의)
            Seg(0, 2, 0.15f, shirt); Seg(5, 12, 0.07f, shirt); Seg(18, 22, 0.08f, pants);
            // 팔: 위팔은 소매, 아래팔과 손은 피부
            Seg(5, 6, 0.05f, shirt); Seg(6, 7, 0.04f, Skin); Seg(7, 9, 0.035f, Skin);
            Seg(12, 13, 0.05f, shirt); Seg(13, 14, 0.04f, Skin); Seg(14, 16, 0.035f, Skin);
            // 목, 머리
            Seg(3, 26, 0.045f, Skin);
            Capsule(p[26 * 2], p[26 * 2 + 1] - px(0.03f), p[26 * 2], p[26 * 2 + 1] - px(0.03f), px(0.105f), Hair, m);
            Capsule(p[26 * 2], p[26 * 2 + 1] + px(0.01f), p[26 * 2], p[26 * 2 + 1] + px(0.01f), px(0.09f), Skin, m);
        }
        Roughen();
        return VideoPacket.Build(VideoPacket.FormatBmp, W, H, Bmp(), W, H, _rough);
    }

    /// <summary>
    /// 실제 카메라처럼 거친 마스크 만들기: 깊이 해상도(약 0.6배)로 뭉개고,
    /// 경계 픽셀은 프레임마다 무작위로 뒤집히고(떨림), 안쪽에 작은 구멍이 생김.
    /// </summary>
    private void Roughen()
    {
        const float s = 0.6f;
        int gw = (int)(W * s), gh = (int)(H * s);
        bool At(int gx, int gy)
        {
            gx = Math.Clamp(gx, 0, gw - 1); gy = Math.Clamp(gy, 0, gh - 1);
            return _mask[(int)(gy / s) * W + (int)(gx / s)] != 0;
        }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int gx = (int)(x * s), gy = (int)(y * s);
                bool v = At(gx, gy);
                bool edge = v != At(gx + 1, gy) || v != At(gx - 1, gy) || v != At(gx, gy + 1) || v != At(gx, gy - 1);
                if (edge && _rng.NextDouble() < 0.35) v = !v;          // 경계 떨림
                else if (v && _rng.NextDouble() < 0.012) v = false;     // 작은 구멍
                _rough[y * W + x] = v ? (byte)255 : (byte)0;
            }
    }

    /// <summary>두께 있는 선분(캡슐)을 원통처럼 음영을 넣어 칠함</summary>
    private void Capsule(float x0, float y0, float x1, float y1, float r, (byte r, byte g, byte b) col, byte maskValue)
    {
        if (x0 < 0 || x1 < 0 || r < 0.5f) return;
        int minX = Math.Max(0, (int)(MathF.Min(x0, x1) - r)), maxX = Math.Min(W - 1, (int)(MathF.Max(x0, x1) + r));
        int minY = Math.Max(0, (int)(MathF.Min(y0, y1) - r)), maxY = Math.Min(H - 1, (int)(MathF.Max(y0, y1) + r));
        float dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float t = len2 > 0 ? Math.Clamp(((x - x0) * dx + (y - y0) * dy) / len2, 0, 1) : 0;
                float ex = x - (x0 + t * dx), ey = y - (y0 + t * dy);
                float d2 = (ex * ex + ey * ey) / (r * r);
                if (d2 > 1) continue;
                float shade = 1.08f - 0.45f * d2 - 0.12f * (ex / r); // 왼쪽 위에서 빛
                int i = (y * W + x) * 3;
                _rgb[i] = (byte)Math.Clamp(col.r * shade, 0, 255);
                _rgb[i + 1] = (byte)Math.Clamp(col.g * shade, 0, 255);
                _rgb[i + 2] = (byte)Math.Clamp(col.b * shade, 0, 255);
                _mask[y * W + x] = maskValue;
            }
    }

    /// <summary>24비트 BMP (아래에서 위로, BGR)</summary>
    private byte[] Bmp()
    {
        const int header = 54;
        int rowBytes = W * 3; // 640*3 은 4의 배수
        var bmp = new byte[header + rowBytes * H];
        void U32(int o, int v) => BitConverter.TryWriteBytes(bmp.AsSpan(o), v);
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        U32(2, bmp.Length); U32(10, header); U32(14, 40); U32(18, W); U32(22, H);
        bmp[26] = 1; bmp[28] = 24; U32(34, rowBytes * H);
        for (int y = 0; y < H; y++)
        {
            int src = y * W * 3, dst = header + (H - 1 - y) * rowBytes;
            for (int x = 0; x < W; x++, src += 3, dst += 3)
            {
                bmp[dst] = _rgb[src + 2]; bmp[dst + 1] = _rgb[src + 1]; bmp[dst + 2] = _rgb[src];
            }
        }
        return bmp;
    }
}
