using Microsoft.Azure.Kinect.BodyTracking;
using Microsoft.Azure.Kinect.Sensor;

namespace FemtoSkeletonAgent;

public interface ISkeletonSource
{
    void Run(CancellationToken ct);
}

/// <summary>
/// Femto Bolt (Orbbec K4A Wrapper) + Azure Kinect Body Tracking SDK 1.1.2.
/// 카메라가 빠져 있거나 오류가 나면 3초마다 다시 연결을 시도합니다.
/// </summary>
public sealed class FemtoBoltSkeletonSource : ISkeletonSource
{
    private readonly SkeletonHub _hub;
    private readonly TrackerProcessingMode _mode;

    public FemtoBoltSkeletonSource(SkeletonHub hub, TrackerProcessingMode mode)
    {
        _hub = hub;
        _mode = mode;
    }

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunOnce(ct);
            }
            catch (DllNotFoundException ex)
            {
                _hub.PublishStatus("error",
                    "DLL을 찾지 못했습니다. orbbec 폴더(Orbbec K4A Wrapper)와 Body Tracking SDK 설치를 확인하세요. " + ex.Message,
                    "camera");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _hub.PublishStatus("error", $"카메라 오류: {ex.Message}", "camera");
            }

            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3))) break;
        }
    }

    private void RunOnce(CancellationToken ct)
    {
        if (Device.GetInstalledCount() == 0)
        {
            _hub.PublishStatus("error", "Femto Bolt를 찾을 수 없습니다. USB 연결과 전원을 확인하세요. (재시도 중)", "camera");
            return;
        }

        _hub.PublishStatus("starting", "Femto Bolt 여는 중...", "camera");
        Device opened;
        try
        {
            opened = Device.Open(0);
        }
        catch (AzureKinectException ex)
        {
            throw new InvalidOperationException(
                "카메라를 열지 못했습니다. 카메라를 쓰는 다른 프로그램을 끄고 USB를 다시 꽂아 보세요. " +
                "orbbec\\k4aviewer.exe 에서도 안 열리면 펌웨어(1.1.2 이상 필요)나 Windows 카메라 권한 문제입니다. " +
                "FemtoSetup.exe 를 다시 실행하면 점검합니다. (" + ex.Message + ")", ex);
        }
        using var device = opened;

        // ① Body Tracking 모델을 먼저 불러옴.
        //    (카메라를 켜 둔 채 모델을 불러오느라 수십 초 동안 영상을 안 읽으면 영상이 아예 멈추는 경우가 있어서 순서를 바꿈)
        var calibration = device.GetCalibration(DepthMode.NFOV_Unbinned, ColorResolution.R720p);
        _hub.PublishStatus("starting", "Body Tracking 모델 로딩 중... (처음엔 수십 초 걸릴 수 있어요)", "camera");
        using var tracker = Tracker.Create(calibration, new TrackerConfiguration
        {
            ProcessingMode = _mode,
            SensorOrientation = SensorOrientation.Default,
        });

        // ② 카메라 켜고 첫 영상이 오는지 확인. 안 오면 설정을 바꿔 다시 시도
        //    1) 컬러·깊이가 같은 순간인 것만  2) 따로 와도 받음  3) 컬러 끄고 깊이만(스켈레톤만)
        var configs = new (DeviceConfiguration Config, bool Color, string Label)[]
        {
            (Make(true, true), true, "컬러+깊이 동시"),
            (Make(true, false), true, "컬러+깊이 따로"),
            (Make(false, false), false, "깊이만"),
        };
        int ci = Math.Min(_goodConfig, configs.Length - 1);
        while (true)
        {
            bool started = false;
            try { device.StartCameras(configs[ci].Config); started = true; }
            catch (AzureKinectException ex) { Console.WriteLine($"  [카메라] '{configs[ci].Label}' 설정으로 켤 수 없음: {ex.Message}"); }
            if (started && WaitFirstFrame(device, TimeSpan.FromSeconds(4)))
            {
                Console.WriteLine($"  [카메라] 영상 확인 ({configs[ci].Label})");
                break;
            }
            if (started)
            {
                Console.WriteLine($"  [카메라] '{configs[ci].Label}' 설정으로는 영상이 안 와서 다른 설정으로 바꿉니다.");
                try { device.StopCameras(); } catch { }
            }
            if (++ci >= configs.Length)
                throw new TimeoutException("카메라 영상이 들어오지 않습니다 (모든 설정 시도). USB 3 연결과 그래픽 드라이버를 확인하세요.");
        }
        _goodConfig = ci;
        var config = configs[ci].Config;
        bool colorOn = configs[ci].Color;
        if (!colorOn) Console.WriteLine("  컬러 영상 없이 스켈레톤만 보냅니다.");

        try
        {
            using var transformation = colorOn ? calibration.CreateTransformation() : null;

            // 기울기 센서(IMU): 카메라가 기울어져 있어도 아바타가 똑바로 서도록 중력 방향을 잼
            bool imuOn = false;
            try
            {
                device.StartImu();
                imuOn = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  기울기 센서(IMU)를 켤 수 없어 기울기 보정 없이 진행합니다: {ex.Message}");
            }
            var accelToDepth = calibration.DeviceExtrinsics[(int)CalibrationDeviceType.Accel * (int)CalibrationDeviceType.Num + (int)CalibrationDeviceType.Depth];
            var tilt = new TiltEstimator(accelToDepth.Rotation);
            bool videoWarned = false;

            _hub.PublishStatus("running",
                $"트래킹 중 (Femto Bolt S/N {device.SerialNum})" + (colorOn ? "" : " · 컬러 꺼짐: 스켈레톤만 표시"), "camera");

            var bodies = new List<BodyData>(4);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int missed = 0;                                  // 연속으로 영상이 안 온 횟수 (2초 단위)
            var restarts = new Queue<TimeSpan>();            // 최근 카메라 다시 켠 시각
            while (!ct.IsCancellationRequested)
            {
                Capture capture;
                try
                {
                    capture = device.GetCapture(TimeSpan.FromSeconds(2));
                    if (missed > 0)
                    {
                        Console.WriteLine("  [카메라] 영상이 다시 들어옵니다.");
                        _hub.PublishStatus("running", $"트래킹 중 (Femto Bolt S/N {device.SerialNum})", "camera");
                    }
                    missed = 0;
                }
                catch (TimeoutException)
                {
                    // 영상이 잠깐 끊김: 모델은 그대로 두고 기다렸다가, 계속 안 오면 카메라만 다시 켬 (몇 초면 복구)
                    missed++;
                    Console.WriteLine($"  [카메라] 영상이 {missed * 2}초째 안 들어옵니다. (USB 3 포트에 바로 연결됐는지 확인)");
                    _hub.PublishStatus("starting", "카메라 영상이 잠깐 끊겼어요. 다시 연결 중…", "camera");
                    if (missed < 2) continue;
                    while (restarts.Count > 0 && clock.Elapsed - restarts.Peek() > TimeSpan.FromMinutes(1)) restarts.Dequeue();
                    if (restarts.Count >= 3)                  // 1분에 3번 넘게 끊기면 다음 설정으로 처음부터 다시 열기
                    {
                        _goodConfig = Math.Min(ci + 1, configs.Length - 1);
                        throw;
                    }
                    restarts.Enqueue(clock.Elapsed);
                    Console.WriteLine("  [카메라] 카메라만 다시 켭니다 (Body Tracking 모델은 그대로).");
                    if (imuOn) { try { device.StopImu(); } catch { } }
                    try { device.StopCameras(); } catch { }
                    device.StartCameras(config);
                    if (imuOn) { try { device.StartImu(); } catch { imuOn = false; } }
                    missed = 0;
                    continue;
                }
                using (capture)
                {
                    tracker.EnqueueCapture(capture);
                }

                if (imuOn) imuOn = ReadImu(device, tilt);
                if (tilt.TryGetUpdate(out var up)) _hub.PublishCamera(up, "imu");

                // 나온 결과를 모두 꺼내고 가장 최근 것만 사용 (그래픽카드가 잠깐 밀려도 결과가 쌓이지 않게)
                Frame? latest = null;
                while (true)
                {
                    var next = tracker.PopResult(TimeSpan.Zero, throwOnTimeout: false);
                    if (next == null) break;
                    latest?.Dispose();
                    latest = next;
                }
                if (latest == null) continue;
                using Frame frame = latest;

                bool video = colorOn && _hub.WantsVideo;
                bodies.Clear();
                for (uint i = 0; i < frame.NumberOfBodies; i++)
                {
                    var skeleton = frame.GetBodySkeleton(i);
                    var joints = new float[SkeletonHub.JointCount * 4];
                    var p2 = video ? new float[SkeletonHub.JointCount * 2] : null;
                    for (int j = 0; j < SkeletonHub.JointCount; j++)
                    {
                        var joint = skeleton.GetJoint(j);
                        joints[j * 4 + 0] = joint.Position.X;
                        joints[j * 4 + 1] = joint.Position.Y;
                        joints[j * 4 + 2] = joint.Position.Z;
                        joints[j * 4 + 3] = (int)joint.ConfidenceLevel; // 0 None, 1 Low, 2 Medium, 3 High
                        if (p2 != null)
                        {
                            // 깊이 카메라 3D 좌표 → 컬러 영상 픽셀 좌표
                            var uv = calibration.TransformTo2D(joint.Position, CalibrationDeviceType.Depth, CalibrationDeviceType.Color);
                            p2[j * 2] = uv?.X ?? -1;
                            p2[j * 2 + 1] = uv?.Y ?? -1;
                        }
                    }
                    bodies.Add(new BodyData(frame.GetBodyId(i), joints, p2));
                }

                // 영상을 스켈레톤보다 먼저 보내서 브라우저에서 같은 순간끼리 맞도록
                if (video)
                {
                    try
                    {
                        var packet = BuildVideoPacket(frame, transformation!);
                        if (packet != null) _hub.PublishVideo(packet);
                    }
                    catch (Exception ex) when (!videoWarned)
                    {
                        videoWarned = true;
                        Console.WriteLine($"  영상 처리 오류 (스켈레톤은 계속 보냄): {ex.Message}");
                    }
                    catch { /* 같은 오류 반복 출력 안 함 */ }
                }

                _hub.PublishFrame((ulong)(clock.Elapsed.Ticks / 10), bodies);
            }
        }
        finally
        {
            try { device.StopImu(); } catch { }
            try { device.StopCameras(); } catch { }
        }
    }

    /// <summary>마지막으로 영상이 잘 들어온 카메라 설정 (다시 열 때 이것부터)</summary>
    private int _goodConfig;

    private static DeviceConfiguration Make(bool color, bool syncOnly) => new()
    {
        CameraFPS = FPS.FPS30,
        ColorFormat = ImageFormat.ColorMJPG,                         // 카메라가 압축한 JPEG을 그대로 브라우저로
        ColorResolution = color ? ColorResolution.R720p : ColorResolution.Off,
        DepthMode = DepthMode.NFOV_Unbinned,                         // Body Tracking 권장 모드
        SynchronizedImagesOnly = color && syncOnly,                  // 컬러와 깊이가 같은 순간의 것만
        WiredSyncMode = WiredSyncMode.Standalone,
    };

    /// <summary>카메라를 켠 뒤 첫 영상이 오는지 (그 영상은 버림)</summary>
    private static bool WaitFirstFrame(Device device, TimeSpan timeout)
    {
        try { using var c = device.GetCapture(timeout); return true; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>쌓인 IMU 샘플을 모두 읽어 기울기 추정에 넣음. IMU에 문제가 생기면 false (더 읽지 않음).</summary>
    private static bool ReadImu(Device device, TiltEstimator tilt)
    {
        try
        {
            for (int i = 0; i < 200; i++)
                tilt.Add(device.GetImuSample(TimeSpan.Zero).AccelerometerSample);
        }
        catch (TimeoutException) { /* 지금 읽을 샘플 없음 */ }
        catch (Exception ex)
        {
            Console.WriteLine($"  기울기 센서(IMU) 읽기 오류, 기울기 보정을 멈춥니다: {ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 컬러 JPEG + 사람 영역 마스크.
    /// Body Tracking의 body index map(깊이 해상도, 배경 255)을 컬러 카메라 좌표로 옮긴 뒤 절반으로 줄여 보냅니다.
    /// </summary>
    private static byte[]? BuildVideoPacket(Frame frame, Transformation transformation)
    {
        // Frame이 소유한 캡처/이미지 (Microsoft 샘플과 같이 따로 Dispose 하지 않음)
        var capture = frame.Capture;
        var color = capture?.Color;
        var depth = capture?.Depth;
        var bodyIndex = GetBodyIndexMap(frame);
        if (color == null || depth == null || bodyIndex == null) return null;

        var jpeg = color.Memory.Span.Slice(0, (int)color.Size);
        var (tDepth, tIndex) = transformation.DepthImageToColorCameraCustom(
            depth, bodyIndex, TransformationInterpolationType.Nearest, 255);
        using (tDepth)
        using (tIndex)
        {
            var mask = VideoPacket.DownsampleBodyIndex(tIndex.Memory.Span, tIndex.WidthPixels, tIndex.HeightPixels,
                tIndex.StrideBytes, 2, out int mw, out int mh);
            return VideoPacket.Build(VideoPacket.FormatJpeg, color.WidthPixels, color.HeightPixels, jpeg, mw, mh, mask);
        }
    }

    private static System.Reflection.PropertyInfo? _bodyIndexProp;
    private static bool _bodyIndexWarned;

    /// <summary>Frame.BodyIndexMap (Body Tracking C# SDK). 버전에 따라 이름이 다를 수 있어 리플렉션으로 찾습니다.</summary>
    private static Image? GetBodyIndexMap(Frame frame)
    {
        _bodyIndexProp ??= typeof(Frame).GetProperty("BodyIndexMap");
        if (_bodyIndexProp?.GetValue(frame) is Image img) return img;
        if (!_bodyIndexWarned)
        {
            _bodyIndexWarned = true;
            Console.WriteLine("  Body index map을 가져올 수 없어 배경 제거 영상을 보낼 수 없습니다. (이 메시지를 알려 주세요)");
        }
        return null;
    }
}

/// <summary>
/// 카메라 없이 웹 화면을 개발/테스트하기 위한 가짜 스켈레톤.
/// 실제 카메라와 같은 좌표계(mm, x 오른쪽, y 아래, z 앞)와 같은 메시지 형식을 씁니다.
/// </summary>
public sealed class DemoSkeletonSource : ISkeletonSource
{
    private readonly SkeletonHub _hub;
    private readonly DemoVideo _video = new();
    public DemoSkeletonSource(SkeletonHub hub) => _hub = hub;

    // 템플릿: 미터 단위, y 위(+), 바닥 0, 사람 기준 왼쪽(+x) — 카메라를 마주보고 선 자세
    // 사람의 왼쪽은 카메라 화면에서 오른쪽(+x)에 보입니다.
    private static readonly (float x, float y, float z)[] Template =
    {
        (0f, 1.00f, 0f),        // 0 Pelvis
        (0f, 1.15f, 0f),        // 1 SpineNavel
        (0f, 1.32f, 0f),        // 2 SpineChest
        (0f, 1.50f, 0f),        // 3 Neck
        (0.04f, 1.45f, 0f),     // 4 ClavicleLeft
        (0.18f, 1.44f, 0f),     // 5 ShoulderLeft
        (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), // 6~10 왼팔 (계산)
        (-0.04f, 1.45f, 0f),    // 11 ClavicleRight
        (-0.18f, 1.44f, 0f),    // 12 ShoulderRight
        (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), (0f, 0f, 0f), // 13~17 오른팔 (계산)
        (0.09f, 0.97f, 0f),     // 18 HipLeft
        (0.10f, 0.53f, 0.02f),  // 19 KneeLeft
        (0.10f, 0.10f, 0f),     // 20 AnkleLeft
        (0.10f, 0.03f, 0.12f),  // 21 FootLeft
        (-0.09f, 0.97f, 0f),    // 22 HipRight
        (-0.10f, 0.53f, 0.02f), // 23 KneeRight
        (-0.10f, 0.10f, 0f),    // 24 AnkleRight
        (-0.10f, 0.03f, 0.12f), // 25 FootRight
        (0f, 1.62f, 0.01f),     // 26 Head
        (0f, 1.63f, 0.10f),     // 27 Nose
        (0.035f, 1.67f, 0.08f), // 28 EyeLeft
        (0.075f, 1.64f, 0f),    // 29 EarLeft
        (-0.035f, 1.67f, 0.08f),// 30 EyeRight
        (-0.075f, 1.64f, 0f),   // 31 EarRight
    };

    public void Run(CancellationToken ct)
    {
        _hub.PublishStatus("running", "데모 모드: 가짜 스켈레톤 2명 (한 명은 주기적으로 나갔다 들어옴, 카메라는 일부러 기울임)", "demo");
        var lastCamera = TimeSpan.FromSeconds(-10);   // 처음 프레임에 바로 보내도록
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bodies = new List<BodyData>(2);
        var period = TimeSpan.FromMilliseconds(1000.0 / 30);
        var next = sw.Elapsed;
        var rng = new Random(7);

        // 두 번째 사람: 14초 보이고 5초 사라짐. 다시 나타날 때마다 실제 카메라처럼 body id가 새로 바뀜
        const float visible = 14f, hidden = 5f;
        uint secondId = 2;
        bool secondWasVisible = true;

        while (!ct.IsCancellationRequested)
        {
            float t = (float)sw.Elapsed.TotalSeconds;
            bodies.Clear();
            var a = Tilt(BuildPose(t, scale: 1.0f, baseX: -0.45f, phase: 0f, rng));
            bodies.Add(new BodyData(1, a, DemoVideo.Project(a)));

            bool secondVisible = (t % (visible + hidden)) < visible;
            if (secondVisible && !secondWasVisible) secondId++;
            secondWasVisible = secondVisible;
            if (secondVisible)
            {
                var b = Tilt(BuildPose(t, scale: 0.84f, baseX: 0.55f, phase: 2.1f, rng));
                bodies.Add(new BodyData(secondId, b, DemoVideo.Project(b)));
            }

            if (_hub.WantsVideo) _hub.PublishVideo(_video.Render(bodies));
            _hub.PublishFrame((ulong)(sw.Elapsed.Ticks / 10), bodies);

            // 실제 카메라의 IMU처럼 2초마다 기울기 전송
            if (sw.Elapsed - lastCamera > TimeSpan.FromSeconds(2))
            {
                lastCamera = sw.Elapsed;
                var up = TiltVec(0, -1, 0);
                _hub.PublishCamera(new[] { up.x, up.y, up.z }, "demo");
            }

            next += period;
            var wait = next - sw.Elapsed;
            if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait)) break;
        }
    }

    /// <param name="scale">체형 크기 (키 비율). 사람마다 다르게 해서 재식별을 시험</param>
    private static float[] BuildPose(float t, float scale, float baseX, float phase, Random rng)
    {
        var p = new (float x, float y, float z)[SkeletonHub.JointCount];
        Array.Copy(Template, p, p.Length);
        t += phase;

        // 무릎 굽혔다 펴기 (스쿼트 느낌)
        float squat = 0.10f * (0.5f - 0.5f * MathF.Cos(t * 1.1f));
        for (int i = 0; i < p.Length; i++)
        {
            if (i is 20 or 21 or 24 or 25) continue; // 발목/발은 바닥에 고정
            if (i is 19 or 23) { p[i].y -= squat * 0.5f; p[i].z += squat * 0.6f; continue; } // 무릎은 앞으로
            p[i].y -= squat;
        }

        // 왼팔: 천천히 흔들기 / 오른팔: 들어서 손 흔들기
        PlaceArm(p, shoulder: 5, side: +1, upperDeg: 25 + 15 * MathF.Sin(t * 0.9f), foreDeg: 35 + 15 * MathF.Sin(t * 0.9f));
        PlaceArm(p, shoulder: 12, side: -1, upperDeg: 100, foreDeg: 160 + 30 * MathF.Sin(t * 6f));

        // 좌우로 걸어다니기 + 살짝 회전
        float sway = baseX + 0.25f * MathF.Sin(t * 0.45f);
        float yaw = 0.25f * MathF.Sin(t * 0.3f);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);

        const float personDistance = 2.6f; // 카메라에서 2.6m
        const float cameraHeight = 1.0f;   // 카메라 높이 1m 가정

        var joints = new float[SkeletonHub.JointCount * 4];
        for (int i = 0; i < p.Length; i++)
        {
            float x = p[i].x * scale, y = p[i].y * scale, z = p[i].z * scale; // 바닥 기준으로 체형 크기 조절
            float rx = x * cy + z * sy;
            float rz = -x * sy + z * cy;
            float noise() => (float)(rng.NextDouble() - 0.5) * (i is 9 or 16 or 21 or 25 ? 36f : 20f);   // 실제와 비슷한 떨림 ±10mm, 손끝·발 ±18mm
            joints[i * 4 + 0] = (rx + sway) * 1000f + noise();               // 카메라 x (오른쪽 +)
            joints[i * 4 + 1] = (cameraHeight - y) * 1000f + noise();        // 카메라 y (아래 +)
            joints[i * 4 + 2] = (personDistance - rz) * 1000f + noise();     // 카메라 z (앞 +)
            joints[i * 4 + 3] = 2;                                            // Medium
        }
        return joints;
    }

    // 데모 카메라 기울기: 아래로 8°, 오른쪽으로 5° 기울어진 카메라를 흉내 (기울기 보정 시험용)
    private const float DemoPitchDeg = 8f, DemoRollDeg = 5f;

    /// <summary>수평 카메라 좌표 → 기울어진 카메라 좌표 (카메라 원점 기준 회전)</summary>
    private static (float x, float y, float z) TiltVec(float x, float y, float z)
    {
        float p = DemoPitchDeg * MathF.PI / 180f, r = DemoRollDeg * MathF.PI / 180f;
        // 아래로 내려다봄: x축 회전
        float y1 = MathF.Cos(p) * y - MathF.Sin(p) * z;
        float z1 = MathF.Sin(p) * y + MathF.Cos(p) * z;
        // 옆으로 기움: z축 회전
        float x2 = MathF.Cos(r) * x - MathF.Sin(r) * y1;
        float y2 = MathF.Sin(r) * x + MathF.Cos(r) * y1;
        return (x2, y2, z1);
    }

    private static float[] Tilt(float[] joints)
    {
        for (int i = 0; i < SkeletonHub.JointCount; i++)
        {
            var v = TiltVec(joints[i * 4], joints[i * 4 + 1], joints[i * 4 + 2]);
            joints[i * 4] = v.x; joints[i * 4 + 1] = v.y; joints[i * 4 + 2] = v.z;
        }
        return joints;
    }

    /// <summary>어깨 → 팔꿈치 → 손목 → 손을 각도로 배치. 0도 = 아래로 늘어뜨림, 180도 = 머리 위.</summary>
    private static void PlaceArm((float x, float y, float z)[] p, int shoulder, int side, float upperDeg, float foreDeg)
    {
        const float upper = 0.28f, fore = 0.25f;
        float a = upperDeg * MathF.PI / 180f, b = foreDeg * MathF.PI / 180f;
        var s = p[shoulder];
        var elbow = (x: s.x + side * upper * MathF.Sin(a), y: s.y - upper * MathF.Cos(a), z: s.z + 0.03f);
        var dir = (x: side * MathF.Sin(b), y: -MathF.Cos(b));
        var wrist = (x: elbow.x + fore * dir.x, y: elbow.y + fore * dir.y, z: elbow.z + 0.02f);

        p[shoulder + 1] = elbow;                                                           // Elbow
        p[shoulder + 2] = wrist;                                                           // Wrist
        p[shoulder + 3] = (wrist.x + 0.07f * dir.x, wrist.y + 0.07f * dir.y, wrist.z);     // Hand
        p[shoulder + 4] = (wrist.x + 0.14f * dir.x, wrist.y + 0.14f * dir.y, wrist.z);     // HandTip
        p[shoulder + 5] = (wrist.x + 0.06f * dir.x, wrist.y + 0.06f * dir.y, wrist.z + 0.05f); // Thumb
    }
}
