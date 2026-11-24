using System.Numerics;

namespace FemtoSkeletonAgent;

/// <summary>
/// 카메라 기울기 추정 (Femto Bolt 내장 IMU 가속도계).
/// Microsoft floor_detector 샘플과 같은 방식:
///  - 가만히 있는 가속도계는 중력 반대 방향(위쪽)으로 약 9.81 m/s² 를 잽니다.
///  - 가속도계 좌표 → 깊이 카메라 좌표로 회전(캘리브레이션 외부 파라미터)하면
///    깊이 카메라 기준 "위쪽" 방향이 나오고, 관절 좌표도 깊이 카메라 기준이라 그대로 보정에 씁니다.
/// 결과: 깊이 카메라 좌표(x 오른쪽, y 아래, z 앞)의 위쪽 단위 벡터. 카메라가 수평이면 약 (0, -1, 0).
/// </summary>
public sealed class TiltEstimator
{
    private readonly float[] _r;           // 가속도계 → 깊이 카메라 회전 (3x3, 행 우선)
    private Vector3 _up;
    private int _count;
    private Vector3 _published;
    private DateTime _lastPublish = DateTime.MinValue;

    public TiltEstimator(float[] accelToDepthRotation) => _r = accelToDepthRotation;

    /// <summary>가속도 샘플 (m/s²). 움직이는 중(크기가 중력과 다름)이면 무시.</summary>
    public void Add(Vector3 acc)
    {
        float len = acc.Length();
        if (MathF.Abs(len - 9.81f) > 0.4f) return;
        var d = new Vector3(
            _r[0] * acc.X + _r[1] * acc.Y + _r[2] * acc.Z,
            _r[3] * acc.X + _r[4] * acc.Y + _r[5] * acc.Z,
            _r[6] * acc.X + _r[7] * acc.Y + _r[8] * acc.Z) / len;
        _up = _count == 0 ? d : Vector3.Normalize(_up * 0.98f + d * 0.02f);   // 천천히 평균 (손 떨림·진동 무시)
        _count++;
    }

    /// <summary>보낼 때가 되면(처음, 0.3° 이상 바뀜, 또는 2초마다) 위쪽 방향을 돌려줍니다.</summary>
    public bool TryGetUpdate(out float[] up)
    {
        up = Array.Empty<float>();
        if (_count < 20) return false;
        var now = DateTime.UtcNow;
        bool changed = _lastPublish == DateTime.MinValue ||
                       MathF.Acos(Math.Clamp(Vector3.Dot(_up, _published), -1f, 1f)) > 0.3f * MathF.PI / 180f;
        if (!changed && now - _lastPublish < TimeSpan.FromSeconds(2)) return false;
        if (_lastPublish == DateTime.MinValue)
            Console.WriteLine($"  카메라 기울기: {Describe(_up)}");
        _published = _up;
        _lastPublish = now;
        up = new[] { _up.X, _up.Y, _up.Z };
        return true;
    }

    /// <summary>사람이 읽는 기울기 설명 (앞뒤 / 좌우)</summary>
    public static string Describe(Vector3 up)
    {
        // 수평 카메라: up = (0,-1,0). 아래를 내려다보면 위쪽이 카메라 뒤쪽(-z)으로 기움.
        float pitch = MathF.Atan2(-up.Z, -up.Y) * 180f / MathF.PI;   // + = 아래로 내려다봄
        float roll = MathF.Atan2(up.X, -up.Y) * 180f / MathF.PI;     // + = 오른쪽으로 기움
        return $"{(pitch >= 0 ? "아래로" : "위로")} {MathF.Abs(pitch):0.0}°, {(roll >= 0 ? "오른쪽으로" : "왼쪽으로")} {MathF.Abs(roll):0.0}°";
    }
}
