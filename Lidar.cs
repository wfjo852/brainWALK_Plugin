using System.IO.Ports;
using System.Runtime.InteropServices;

namespace BrainWalk;

internal readonly record struct ScanPoint(double Distance, double Angle);
internal readonly record struct TouchPoint(double X, double Y);
internal enum TouchGesture { Touch, Drag }
internal readonly record struct TouchContact(int Id, TouchPoint Position, TouchGesture Gesture);

internal static class LidarParser
{
    private static int Word(byte[] data, int offset) => data[offset] | data[offset + 1] << 8;

    public static bool TryParse(byte[] packet, out ScanPoint[] points)
    {
        points = [];
        if (packet.Length < 10 || packet[0] != 0xAA || packet[1] != 0x55) return false;
        int count = packet[3];
        if (packet.Length != 10 + count * 2 || count == 0) return false;
        int checksum = 0x55AA ^ Word(packet, 2) ^ Word(packet, 4) ^ Word(packet, 6);
        for (int i = 0; i < count; i++) checksum ^= Word(packet, 10 + 2 * i);
        if (checksum != Word(packet, 8)) return false;
        double first = (Word(packet, 4) >> 1) / 64.0;
        double last = (Word(packet, 6) >> 1) / 64.0;
        double diff = last >= first ? last - first : 360 + last - first;
        points = new ScanPoint[count];
        for (int i = 0; i < count; i++)
        {
            double distance = Word(packet, 10 + i * 2) / 4.0;
            double angle = (first + diff * i / count) % 360;
            if (distance != 0) angle += Math.Atan(21.8 * (155.3 - distance) / (155.3 * distance)) * 180 / Math.PI;
            points[i] = new(distance, (angle + 360) % 360);
        }
        return true;
    }
}

internal sealed class LidarService : IDisposable
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private static readonly byte[] StartScanCommand = [0xA5, 0x60];
    private static readonly byte[] StopScanCommand = [0xA5, 0x65];
    private static readonly byte[] GetFrequencyCommand = [0xA5, 0x0D];
    private static readonly byte[] FrequencyUpPointOneCommand = [0xA5, 0x09];
    private static readonly byte[] FrequencyDownPointOneCommand = [0xA5, 0x0A];
    private static readonly byte[] FrequencyUpOneCommand = [0xA5, 0x0B];
    private static readonly byte[] FrequencyDownOneCommand = [0xA5, 0x0C];
    private SerialPort? _port;
    private CancellationTokenSource? _stop;
    private Task? _readTask;
    private readonly SemaphoreSlim _frequencyLock = new(1, 1);
    public event Action<ScanPoint[], IReadOnlyList<TouchContact>>? Frame;
    public event Action<string>? Status;
    public bool IsRunning => _port?.IsOpen == true;
    public bool MouseEnabled { get; set; }
    public bool DragEnabled { get; set; } = true;
    public Rectangle TargetScreenBounds { get; set; } = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    public int Threshold { get; set; } = 1000;
    public int MinPoints { get; set; } = 1;
    public int StartAngle { get; set; } = 0;
    public int EndAngle { get; set; } = 180;
    public int Rotation { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public double AreaX { get; set; } = -300;
    public double AreaY { get; set; } = 330;
    public double AreaWidth { get; set; } = 600;
    public double AreaHeight { get; set; } = -300;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double GroupNeighborDistance { get; set; } = 50.0;
    public double MaximumTouchGroupDiameter { get; set; } = 200.0;
    public double ScanFrequency { get; private set; } = 7.0;
    private readonly double[] _distances = new double[360];
    private double _lastAngle = -1;
    private int _nextContactId = 1;
    private ContactTrack? _contactSession;
    private int? _primaryContactId;
    private Point _primaryScreenPoint;
    private bool _leftButtonHeld;

    private sealed class ContactTrack(int id, TouchPoint position)
    {
        public int Id { get; } = id;
        public TouchPoint Start { get; } = position;
        public TouchPoint Position { get; set; } = position;
        public TouchPoint LastRawPosition { get; set; } = position;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public TouchGesture Gesture { get; set; } = TouchGesture.Touch;
    }

    public async Task StartAsync(string portName, int baudRate, double scanFrequency)
    {
        if (IsRunning) return;
        _port = new SerialPort(portName, baudRate) { ReadTimeout = 500, WriteTimeout = 500 };
        try
        {
            _port.Open();
            double actual = await SetScanFrequencyCoreAsync(scanFrequency);
            _stop = new();
            _port.Write(StartScanCommand, 0, StartScanCommand.Length);
            _readTask = Task.Run(() => ReadLoop(_stop.Token));
            Status?.Invoke($"LiDAR 연결됨: {portName}, 스캔 {actual:F1}Hz ({actual * 60:F0}RPM)");
        }
        catch
        {
            _port.Dispose();
            _port = null;
            throw;
        }
    }

    public async Task<double> SetScanFrequencyAsync(double targetFrequency)
    {
        targetFrequency = Math.Clamp(targetFrequency, 5.4, 11.9);
        await _frequencyLock.WaitAsync();
        try
        {
            if (_port?.IsOpen != true)
            {
                ScanFrequency = targetFrequency;
                return ScanFrequency;
            }

            bool restartScan = _stop is { IsCancellationRequested: false };
            if (restartScan)
            {
                _stop!.Cancel();
                try { if (_readTask is not null) await _readTask; } catch (OperationCanceledException) { }
                _port.Write(StopScanCommand, 0, StopScanCommand.Length);
                await Task.Delay(120);
            }

            double actual = await SetScanFrequencyCoreAsync(targetFrequency);
            if (restartScan)
            {
                _stop = new CancellationTokenSource();
                _port.Write(StartScanCommand, 0, StartScanCommand.Length);
                _readTask = Task.Run(() => ReadLoop(_stop.Token));
            }
            Status?.Invoke($"스캔 속도 적용: {actual:F1}Hz ({actual * 60:F0}RPM)");
            return actual;
        }
        finally { _frequencyLock.Release(); }
    }

    private async Task<double> SetScanFrequencyCoreAsync(double targetFrequency)
    {
        targetFrequency = Math.Clamp(targetFrequency, 5.4, 11.9);
        double current = await SendFrequencyCommandAsync(GetFrequencyCommand);
        if (current <= 0) current = ScanFrequency;
        for (int attempt = 0; attempt < 20 && Math.Abs(targetFrequency - current) >= 0.15; attempt++)
        {
            double difference = targetFrequency - current;
            byte[] command = difference > 0
                ? (difference >= 1.0 ? FrequencyUpOneCommand : FrequencyUpPointOneCommand)
                : (difference <= -1.0 ? FrequencyDownOneCommand : FrequencyDownPointOneCommand);
            double changed = await SendFrequencyCommandAsync(command);
            if (changed <= 0) break;
            current = changed;
            await Task.Delay(50);
        }
        ScanFrequency = current > 0 ? current : targetFrequency;
        return ScanFrequency;
    }

    private async Task<double> SendFrequencyCommandAsync(byte[] command)
    {
        if (_port?.IsOpen != true) return -1;
        _port.DiscardInBuffer();
        await Task.Delay(50);
        _port.Write(command, 0, command.Length);
        await Task.Delay(100);
        var response = new byte[11];
        int offset = 0;
        try
        {
            while (offset < response.Length)
            {
                int count = _port.Read(response, offset, response.Length - offset);
                if (count <= 0) break;
                offset += count;
            }
        }
        catch (TimeoutException) { }
        if (offset < 11 || response[0] != 0xA5 || response[1] != 0x5A) return -1;
        uint raw = BitConverter.ToUInt32(response, 7);
        return raw / 100.0;
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new List<byte>(8192);
        var chunk = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested && _port?.IsOpen == true)
            {
                int count;
                try { count = _port.Read(chunk, 0, chunk.Length); }
                catch (TimeoutException) { continue; }
                for (int i = 0; i < count; i++) buffer.Add(chunk[i]);
                while (buffer.Count >= 10)
                {
                    if (buffer[0] != 0xAA || buffer[1] != 0x55) { buffer.RemoveAt(0); continue; }
                    int size = 10 + 2 * buffer[3];
                    if (size > 520) { buffer.RemoveAt(0); continue; }
                    if (buffer.Count < size) break;
                    var packet = buffer.GetRange(0, size).ToArray();
                    if (!LidarParser.TryParse(packet, out var points)) { buffer.RemoveAt(0); continue; }
                    buffer.RemoveRange(0, size);
                    ScanPoint[]? completedScan = null;
                    foreach (var point in points)
                    {
                        if (_lastAngle > 300 && point.Angle < 60)
                        {
                            completedScan = _distances
                                .Select((distance, angle) => new ScanPoint(distance, angle))
                                .Where(scanPoint => scanPoint.Distance > 0)
                                .ToArray();
                            Array.Clear(_distances);
                        }
                        _distances[(int)Math.Round(point.Angle) % 360] = point.Distance;
                        _lastAngle = point.Angle;
                    }
                    if (completedScan is null) continue;

                    // Group detection, UI updates and mouse input run once per
                    // completed revolution instead of once per serial packet.
                    var contacts = UpdateContactSession(DetectGroups(completedScan));
                    Frame?.Invoke(completedScan, contacts);
                    if (MouseEnabled)
                        ProcessMouseContact(contacts.FirstOrDefault());
                    else ReleaseMouseContact();
                }
            }
        }
        catch (Exception ex) { Status?.Invoke($"LiDAR error: {ex.Message}"); }
    }

    private List<TouchPoint> DetectGroups(IReadOnlyList<ScanPoint> scan)
    {
        var candidates = new List<TouchPoint>();
        foreach (var scanPoint in scan)
        {
            int angle = (int)Math.Round(scanPoint.Angle) % 360;
            bool inScan = StartAngle <= EndAngle ? angle >= StartAngle && angle <= EndAngle : angle >= StartAngle || angle <= EndAngle;
            double distance = scanPoint.Distance;
            if (!inScan || distance <= 0 || distance >= Threshold) continue;
            double radians = angle * Math.PI / 180;
            double x = distance * Math.Cos(radians), y = distance * Math.Sin(radians);
            (x, y) = ((Rotation % 360 + 360) % 360) switch
            {
                90 => (y, -x), 180 => (-x, -y), 270 => (-y, x), _ => (x, y)
            };
            if (FlipX) x = -x;
            if (FlipY) y = -y;
            if (x < Math.Min(AreaX, AreaX + AreaWidth) || x > Math.Max(AreaX, AreaX + AreaWidth) ||
                y < Math.Min(AreaY, AreaY + AreaHeight) || y > Math.Max(AreaY, AreaY + AreaHeight)) continue;
            candidates.Add(new TouchPoint(x, y));
        }

        if (candidates.Count < MinPoints) return [];
        var visited = new bool[candidates.Count];
        var centers = new List<TouchPoint>();
        double neighborSquared = GroupNeighborDistance * GroupNeighborDistance;

        for (int start = 0; start < candidates.Count; start++)
        {
            if (visited[start]) continue;
            var group = new List<TouchPoint>();
            var queue = new Queue<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                var point = candidates[index];
                group.Add(point);
                for (int other = 0; other < candidates.Count; other++)
                {
                    if (visited[other]) continue;
                    double dx = point.X - candidates[other].X;
                    double dy = point.Y - candidates[other].Y;
                    if (dx * dx + dy * dy <= neighborSquared)
                    {
                        visited[other] = true;
                        queue.Enqueue(other);
                    }
                }
            }

            if (group.Count < MinPoints || GroupDiameter(group) >= MaximumTouchGroupDiameter) continue;
            centers.Add(new TouchPoint(
                group.Average(point => point.X) + OffsetX,
                group.Average(point => point.Y) + OffsetY));
        }
        return centers.OrderBy(point => Math.Sqrt(point.X * point.X + point.Y * point.Y)).ToList();
    }

    private static double GroupDiameter(List<TouchPoint> group)
    {
        double maximumSquared = 0;
        for (int first = 0; first < group.Count; first++)
            for (int second = first + 1; second < group.Count; second++)
            {
                double dx = group[first].X - group[second].X;
                double dy = group[first].Y - group[second].Y;
                maximumSquared = Math.Max(maximumSquared, dx * dx + dy * dy);
            }
        return Math.Sqrt(maximumSquared);
    }

    private IReadOnlyList<TouchContact> UpdateContactSession(List<TouchPoint> detections)
    {
        var now = DateTime.UtcNow;
        if (_contactSession is null)
        {
            if (detections.Count == 0) return [];
            _contactSession = new ContactTrack(_nextContactId++, detections[0]);
        }

        var session = _contactSession;
        TouchPoint? matched = detections
            .Select(point => (Point: point, Distance: Distance(session.LastRawPosition, point)))
            .Where(item => item.Distance <= 150.0)
            .OrderBy(item => item.Distance)
            .Select(item => (TouchPoint?)item.Point)
            .FirstOrDefault();

        if (matched is { } raw)
        {
            session.LastSeenAt = now;
            session.LastRawPosition = raw;
            if (DragEnabled && session.Gesture == TouchGesture.Touch && Distance(session.Start, raw) >= 50.0)
                session.Gesture = TouchGesture.Drag;

            if (DragEnabled && session.Gesture == TouchGesture.Drag)
            {
                const double deadZoneMillimeters = 3.0;
                const double smoothing = 0.25;
                if (Distance(session.Position, raw) >= deadZoneMillimeters)
                    session.Position = new TouchPoint(
                        session.Position.X + (raw.X - session.Position.X) * smoothing,
                        session.Position.Y + (raw.Y - session.Position.Y) * smoothing);
            }
            // Touch Down 상태에서는 최초 중심 좌표를 유지한다.
        }
        else if ((now - session.LastSeenAt).TotalMilliseconds >= 250)
        {
            _contactSession = null;
            return [];
        }

        return [new TouchContact(session.Id, session.Position, session.Gesture)];
    }

    private static double Distance(TouchPoint first, TouchPoint second)
    {
        double dx = first.X - second.X, dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private Point ToScreenPoint(TouchPoint hit)
    {
        var screen = TargetScreenBounds;
        if (AreaWidth == 0 || AreaHeight == 0) return new Point(screen.Left, screen.Top);
        int x = screen.Left + Math.Clamp((int)((hit.X - AreaX) / AreaWidth * screen.Width), 0, screen.Width - 1);
        int y = screen.Top + Math.Clamp((int)((hit.Y - AreaY) / AreaHeight * screen.Height), 0, screen.Height - 1);
        return new Point(x, y);
    }

    private void ProcessMouseContact(TouchContact contact)
    {
        if (contact.Id == 0)
        {
            ReleaseMouseContact();
            return;
        }
        if (_primaryContactId != contact.Id)
        {
            ReleaseMouseContact();
            var downPoint = ToScreenPoint(contact.Position);
            SetCursorPos(downPoint.X, downPoint.Y);
            _primaryContactId = contact.Id;
            _primaryScreenPoint = downPoint;
            HoldLeftButton();
            return;
        }

        var point = ToScreenPoint(contact.Position);
        SetCursorPos(point.X, point.Y);
        _primaryScreenPoint = point;
    }

    private void HoldLeftButton()
    {
        if (_leftButtonHeld) return;
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        _leftButtonHeld = true;
    }

    private void ReleaseMouseContact()
    {
        if (_leftButtonHeld)
        {
            SetCursorPos(_primaryScreenPoint.X, _primaryScreenPoint.Y);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            _leftButtonHeld = false;
        }
        _primaryContactId = null;
    }

    public void Stop()
    {
        ReleaseMouseContact();
        _contactSession = null;
        _stop?.Cancel();
        if (_port?.IsOpen == true)
        {
            try { _port.Write(StopScanCommand, 0, StopScanCommand.Length); } catch { }
            _port.Close();
        }
        _port?.Dispose(); _port = null;
        Status?.Invoke("LiDAR stopped");
    }

    public void Dispose() { Stop(); _frequencyLock.Dispose(); }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}

