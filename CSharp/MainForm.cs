using System.IO.Ports;
using System.Runtime.InteropServices;

namespace BrainWalk;

internal enum PreviewMode { TouchOnly, Full, None }

public sealed class MainForm : Form
{
    private const int MouseHotkeyId = 0xB701;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private readonly AppConfig _config = new();
    private readonly LidarService _lidar = new();
    private readonly AntService _ant = new();
    private readonly ComboBox _ports = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _baud = new() { Minimum = 9600, Maximum = 921600, Value = 230400, Increment = 9600, Width = 100 };
    private readonly TrackBar _scanFrequency = new() { Minimum = 54, Maximum = 119, Value = 70, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Width = 270, AutoSize = false, Height = 34 };
    private readonly Label _scanFrequencyValue = new() { Text = "7.0 Hz", Width = 70, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    private readonly NumericUpDown _threshold = Number(1, 20000, 1000);
    private readonly NumericUpDown _minPoints = Number(1, 360, 1);
    private readonly NumericUpDown _areaX = Number(-10000, 10000, -300);
    private readonly NumericUpDown _areaY = Number(-10000, 10000, 330);
    private readonly NumericUpDown _areaWidth = Number(-10000, 10000, 600);
    private readonly NumericUpDown _areaHeight = Number(-10000, 10000, -300);
    private readonly NumericUpDown _touchOffsetX = Number(-1000, 1000, 0);
    private readonly NumericUpDown _touchOffsetY = Number(-1000, 1000, 0);
    private readonly NumericUpDown _startAngle = Number(0, 359, 90);
    private readonly NumericUpDown _endAngle = Number(0, 359, 270);
    private readonly NumericUpDown _rotation = Number(0, 359, 90);
    private readonly CheckBox _flipX = new() { Text = "Flip X", AutoSize = true };
    private readonly CheckBox _flipY = new() { Text = "Flip Y", AutoSize = true };
    private readonly CheckBox _mouse = new() { Text = "마우스 제어 (Ctrl+Shift+M)", AutoSize = true };
    private readonly CheckBox _drag = new() { Text = "드래그 사용", AutoSize = true, Checked = true };
    private readonly ComboBox _previewMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _bpm = new() { Text = "-- BPM", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
    private readonly Label _deviceLabel = new() { Text = "ANT 장비: --", AutoSize = true };
    private readonly Label _status = new() { Text = "준비", AutoSize = true };
    private readonly Button _lidarButton = new() { Text = "LiDAR 시작", AutoSize = true };
    private readonly Button _antButton = new() { Text = "ANT 시작", AutoSize = true };
    private readonly ScanView _view = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 20, 32) };
    private int _previewModeValue = (int)PreviewMode.Full;

    public MainForm()
    {
        Text = "brainWALK — LiDAR + ANT+";
        Width = 1200; Height = 780;
        MinimumSize = new Size(900, 560);
        BuildUi();
        LoadSettings();
        RefreshPorts();
        RefreshMonitors();
        _lidar.Frame += (points, contacts) =>
        {
            var mode = (PreviewMode)Volatile.Read(ref _previewModeValue);
            if (mode != PreviewMode.None) SafeUi(() => _view.UpdateScan(points, contacts, mode));
        };
        _lidar.Status += message => SafeUi(() => _status.Text = message);
        _ant.Status += message => SafeUi(() =>
        {
            _status.Text = message;
            if (message.StartsWith("ANT 오류") || message is "ANT 검색 완료" or "ANT 수신 중지")
                _antButton.Text = "ANT 시작";
            if (message == "ANT 수신 중지") _bpm.Text = "-- BPM";
        });
        _ant.BpmReceived += bpm => SafeUi(() => _bpm.Text = $"{bpm} BPM");
        _monitors.SelectedIndexChanged += (_, _) => SaveMonitorSelection();
        foreach (var input in new[] { _threshold, _areaX, _areaY, _areaWidth, _areaHeight, _startAngle, _endAngle, _rotation })
            input.ValueChanged += (_, _) => UpdatePreview();
        _flipX.CheckedChanged += (_, _) => UpdatePreview();
        _flipY.CheckedChanged += (_, _) => UpdatePreview();
        _mouse.CheckedChanged += (_, _) => ApplyMouseControl();
        _drag.CheckedChanged += (_, _) => ApplyDragControl();
        _previewMode.SelectedIndexChanged += (_, _) => ApplyPreviewMode();
        _scanFrequency.ValueChanged += (_, _) => _scanFrequencyValue.Text = $"{ScanFrequency:F1} Hz";
        _scanFrequency.MouseUp += async (_, _) => await ApplyScanFrequencyAsync();
        _scanFrequency.KeyUp += async (_, _) => await ApplyScanFrequencyAsync();
        _touchOffsetX.ValueChanged += (_, _) => ApplyTouchOffset();
        _touchOffsetY.ValueChanged += (_, _) => ApplyTouchOffset();
        FormClosing += (_, _) => { _lidar.Stop(); _ant.Stop(); };
    }

    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Width = 90 };
    private void SafeUi(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, MouseHotkeyId, ModControl | ModShift, (uint)Keys.M);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, MouseHotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == MouseHotkeyId)
            _mouse.Checked = !_mouse.Checked;
        base.WndProc(ref message);
    }

    private void BuildUi()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2
        };
        Controls.Add(split);
        Shown += (_, _) =>
        {
            split.SplitterDistance = Math.Max(350, split.ClientSize.Width - 550);
            split.Panel1MinSize = 350;
            split.Panel2MinSize = 440;
        };
        split.Panel1.Controls.Add(_view);
        var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };
        split.Panel2.Controls.Add(side);
        side.Controls.Add(Heading("LiDAR 연결"));
        side.Controls.Add(Row("포트", _ports, Button("새로고침", RefreshPorts)));
        side.Controls.Add(Row("통신 속도", _baud, Unit("bps")));
        side.Controls.Add(Row("스캔 속도", _scanFrequency, _scanFrequencyValue));
        _lidarButton.Click += (_, _) => ToggleLidar();
        side.Controls.Add(_lidarButton);
        side.Controls.Add(Heading("프리뷰"));
        _previewMode.Items.AddRange(["터치 위치만 보기", "전체 프리뷰", "프리뷰 없음"]);
        side.Controls.Add(Row("표시 방식", _previewMode));
        side.Controls.Add(Heading("터치 감지"));
        side.Controls.Add(Row("거리 임계값", _threshold, Unit("mm")));
        side.Controls.Add(Row("최소 포인트", _minPoints, Unit("개")));
        side.Controls.Add(Row("X / Y", _areaX, Unit("mm"), _areaY, Unit("mm")));
        side.Controls.Add(Row("너비 / 높이", _areaWidth, Unit("mm"), _areaHeight, Unit("mm")));
        side.Controls.Add(Row("터치 Offset X/Y", _touchOffsetX, Unit("mm"), _touchOffsetY, Unit("mm")));
        side.Controls.Add(new Label { Text = "그룹 간격 5cm / 최대 지름 20cm", AutoSize = true, ForeColor = Color.DimGray });
        side.Controls.Add(Row("각도 시작 / 끝", _startAngle, Unit("°"), _endAngle, Unit("°")));
        side.Controls.Add(Row("회전", _rotation, Unit("°")));
        side.Controls.Add(Row("반전", _flipX, _flipY));
        side.Controls.Add(_mouse);
        side.Controls.Add(_drag);
        side.Controls.Add(new Label { Text = "터치: 주황 / 드래그: 자홍", AutoSize = true, ForeColor = Color.DimGray });
        side.Controls.Add(Heading("모니터 선택"));
        side.Controls.Add(Row("모니터", _monitors));
        side.Controls.Add(Button("모니터 검색", RefreshMonitors));
        side.Controls.Add(Button("설정 적용 및 저장", SaveSettings));
        side.Controls.Add(Heading("ANT+ 심박"));
        side.Controls.Add(_deviceLabel);
        side.Controls.Add(_bpm);
        _antButton.Click += (_, _) => ToggleAnt();
        side.Controls.Add(Row("심박 수신", _antButton));
        side.Controls.Add(Button("ANT 장비 검색", OpenAntScan));
        side.Controls.Add(Heading("상태"));
        side.Controls.Add(_status);
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold), Margin = new Padding(3, 14, 3, 5) };
    private static Label Unit(string text) => new() { Text = text, AutoSize = true, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 7, 0) };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }
    private static Control Row(string label, params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Width = 510, Height = 36, WrapContents = false };
        panel.Controls.Add(new Label { Text = label, Width = 120, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
        foreach (var control in controls) panel.Controls.Add(control);
        return panel;
    }

    private void RefreshPorts()
    {
        string previous = _ports.SelectedItem?.ToString() ?? _config.Get("connection", "port", "COM4");
        _ports.Items.Clear();
        _ports.Items.AddRange(SerialPort.GetPortNames().OrderBy(x => x).ToArray());
        if (_ports.Items.Contains(previous)) _ports.SelectedItem = previous;
        else if (_ports.Items.Count > 0) _ports.SelectedIndex = 0;
    }

    private sealed record MonitorChoice(int Index, Screen Screen)
    {
        public override string ToString()
        {
            var b = Screen.Bounds;
            return $"{Index + 1}: {b.Width}x{b.Height} ({b.X},{b.Y}){(Screen.Primary ? " 기본" : "")}";
        }
    }

    private void RefreshMonitors()
    {
        int selected = (_monitors.SelectedItem as MonitorChoice)?.Index
            ?? _config.Get("mouse_control", "monitor_index", 0);
        _monitors.Items.Clear();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++) _monitors.Items.Add(new MonitorChoice(i, screens[i]));
        if (_monitors.Items.Count == 0) { _status.Text = "모니터를 찾지 못했습니다"; return; }
        _monitors.SelectedIndex = Math.Clamp(selected, 0, _monitors.Items.Count - 1);
        ApplyMonitor();
        UpdatePreview();
        _status.Text = $"모니터 {_monitors.Items.Count}개 검색됨";
    }

    private void ApplyMonitor()
    {
        if (_monitors.SelectedItem is MonitorChoice choice)
            _lidar.TargetScreenBounds = choice.Screen.Bounds;
    }

    private void SaveMonitorSelection()
    {
        if (_monitors.SelectedItem is not MonitorChoice monitor) return;
        ApplyMonitor();
        UpdatePreview();
        var bounds = monitor.Screen.Bounds;
        _config.Set("mouse_control", "monitor_index", monitor.Index);
        _config.Set("mouse_control", "screen_width", bounds.Width);
        _config.Set("mouse_control", "screen_height", bounds.Height);
        _config.Set("mouse_control", "screen_offset_x", bounds.X);
        _config.Set("mouse_control", "screen_offset_y", bounds.Y);
        _config.Save();
        _status.Text = $"선택한 모니터: {monitor}";
    }

    private void LoadSettings()
    {
        _baud.Value = Math.Clamp(_config.Get("connection", "baudrate", 230400), (int)_baud.Minimum, (int)_baud.Maximum);
        _scanFrequency.Value = Math.Clamp((int)Math.Round(_config.Get("connection", "scan_frequency", 7.0) * 10), _scanFrequency.Minimum, _scanFrequency.Maximum);
        _threshold.Value = _config.Get("touch_detection", "threshold_mm", 1000);
        _minPoints.Value = _config.Get("touch_detection", "min_points", 1);
        _areaX.Value = _config.Get("touch_area", "x", -300);
        _areaY.Value = _config.Get("touch_area", "y", 330);
        _areaWidth.Value = _config.Get("touch_area", "width", 600);
        _areaHeight.Value = _config.Get("touch_area", "height", -300);
        _touchOffsetX.Value = Math.Clamp((decimal)_config.Get("touch_offset", "x_mm", 0.0), _touchOffsetX.Minimum, _touchOffsetX.Maximum);
        _touchOffsetY.Value = Math.Clamp((decimal)_config.Get("touch_offset", "y_mm", 0.0), _touchOffsetY.Minimum, _touchOffsetY.Maximum);
        _startAngle.Value = _config.Get("lidar_position", "scan_start_angle", 90);
        _endAngle.Value = _config.Get("lidar_position", "scan_end_angle", 270);
        _rotation.Value = _config.Get("lidar_position", "rotation", 90);
        _flipX.Checked = _config.Get("lidar_position", "flip_x", true);
        _flipY.Checked = _config.Get("lidar_position", "flip_y", false);
        _mouse.Checked = _config.Get("mouse_control", "enabled", false);
        _drag.Checked = _config.Get("mouse_control", "drag_enabled", true);
        string previewMode = _config.Get("visualization", "preview_mode", "full");
        _previewMode.SelectedIndex = previewMode switch { "touch" => 0, "none" => 2, _ => 1 };
        _previewModeValue = _previewMode.SelectedIndex;
        _view.SetPreviewMode((PreviewMode)_previewModeValue);
        _ant.SelectedDevice = new(_config.Get("ant_device", "device_id", 0), _config.Get("ant_device", "device_type", 120), _config.Get("ant_device", "transmission_type", 0));
        _deviceLabel.Text = $"ANT 장비: {_ant.SelectedDevice}";
        ApplySettings();
    }

    private void ApplySettings()
    {
        _lidar.Threshold = (int)_threshold.Value;
        _lidar.MinPoints = (int)_minPoints.Value;
        _lidar.AreaX = (int)_areaX.Value; _lidar.AreaY = (int)_areaY.Value;
        _lidar.AreaWidth = (int)_areaWidth.Value; _lidar.AreaHeight = (int)_areaHeight.Value;
        _lidar.StartAngle = (int)_startAngle.Value; _lidar.EndAngle = (int)_endAngle.Value;
        _lidar.Rotation = (int)_rotation.Value;
        _lidar.FlipX = _flipX.Checked; _lidar.FlipY = _flipY.Checked;
        _lidar.MouseEnabled = _mouse.Checked;
        _lidar.DragEnabled = _drag.Checked;
        ApplyMonitor();
        _lidar.OffsetX = (double)_touchOffsetX.Value;
        _lidar.OffsetY = (double)_touchOffsetY.Value;
        UpdatePreview();
    }

    private void ApplyMouseControl()
    {
        _lidar.MouseEnabled = _mouse.Checked;
        _config.Set("mouse_control", "enabled", _mouse.Checked);
        _config.Save();
        _status.Text = _mouse.Checked
            ? "마우스 제어 켜짐 (Ctrl+Shift+M)"
            : "마우스 제어 꺼짐 (Ctrl+Shift+M)";
    }

    private void ApplyTouchOffset()
    {
        _lidar.OffsetX = (double)_touchOffsetX.Value;
        _lidar.OffsetY = (double)_touchOffsetY.Value;
        _config.Set("touch_offset", "x_mm", _lidar.OffsetX);
        _config.Set("touch_offset", "y_mm", _lidar.OffsetY);
        _config.Save();
        UpdatePreview();
        _status.Text = $"터치 Offset 적용: X {_lidar.OffsetX:F0}mm, Y {_lidar.OffsetY:F0}mm";
    }

    private void ApplyDragControl()
    {
        _lidar.DragEnabled = _drag.Checked;
        _config.Set("mouse_control", "drag_enabled", _drag.Checked);
        _config.Save();
        _status.Text = _drag.Checked ? "드래그 사용" : "드래그 사용 안 함";
    }

    private void ApplyPreviewMode()
    {
        if (_previewMode.SelectedIndex < 0) return;
        var mode = (PreviewMode)_previewMode.SelectedIndex;
        Volatile.Write(ref _previewModeValue, (int)mode);
        _view.SetPreviewMode(mode);
        _config.Set("visualization", "preview_mode", mode switch
        {
            PreviewMode.TouchOnly => "touch",
            PreviewMode.None => "none",
            _ => "full"
        });
        _config.Save();
        _status.Text = $"프리뷰: {_previewMode.SelectedItem}";
    }

    private void UpdatePreview()
    {
        _view.Configure(new ScanPreviewSettings(
            (int)_threshold.Value, (int)_areaX.Value, (int)_areaY.Value,
            (int)_areaWidth.Value, (int)_areaHeight.Value,
            (int)_startAngle.Value, (int)_endAngle.Value, (int)_rotation.Value,
            _flipX.Checked, _flipY.Checked,
            _lidar.TargetScreenBounds.Width, _lidar.TargetScreenBounds.Height,
            (int)_touchOffsetX.Value, (int)_touchOffsetY.Value));
    }

    private void SaveSettings()
    {
        ApplySettings();
        _config.Set("connection", "port", _ports.SelectedItem?.ToString() ?? "COM4");
        _config.Set("connection", "baudrate", (int)_baud.Value);
        _config.Set("connection", "scan_frequency", ScanFrequency);
        _config.Set("touch_detection", "threshold_mm", (int)_threshold.Value);
        _config.Set("touch_detection", "min_points", (int)_minPoints.Value);
        _config.Set("touch_area", "x", (int)_areaX.Value); _config.Set("touch_area", "y", (int)_areaY.Value);
        _config.Set("touch_area", "width", (int)_areaWidth.Value); _config.Set("touch_area", "height", (int)_areaHeight.Value);
        _config.Set("touch_offset", "x_mm", (double)_touchOffsetX.Value); _config.Set("touch_offset", "y_mm", (double)_touchOffsetY.Value);
        _config.Set("lidar_position", "scan_start_angle", (int)_startAngle.Value); _config.Set("lidar_position", "scan_end_angle", (int)_endAngle.Value);
        _config.Set("lidar_position", "rotation", (int)_rotation.Value);
        _config.Set("lidar_position", "flip_x", _flipX.Checked); _config.Set("lidar_position", "flip_y", _flipY.Checked);
        _config.Set("mouse_control", "enabled", _mouse.Checked);
        _config.Set("mouse_control", "drag_enabled", _drag.Checked);
        _config.Set("visualization", "preview_mode", ((PreviewMode)_previewModeValue) switch
        {
            PreviewMode.TouchOnly => "touch",
            PreviewMode.None => "none",
            _ => "full"
        });
        SaveMonitorSelection();
        _config.Save();
        _status.Text = "설정 저장됨";
    }

    private async void ToggleLidar()
    {
        if (_lidar.IsRunning) { _lidar.Stop(); _lidarButton.Text = "LiDAR 시작"; return; }
        if (_ports.SelectedItem is null) { MessageBox.Show("시리얼 포트를 선택하세요."); return; }
        try
        {
            ApplySettings();
            _lidarButton.Enabled = false;
            _status.Text = $"LiDAR 연결 및 스캔 속도 {ScanFrequency:F1}Hz 적용 중...";
            await _lidar.StartAsync(_ports.SelectedItem.ToString()!, (int)_baud.Value, ScanFrequency);
            _scanFrequency.Value = Math.Clamp((int)Math.Round(_lidar.ScanFrequency * 10), _scanFrequency.Minimum, _scanFrequency.Maximum);
            _lidarButton.Text = "LiDAR 중지";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "LiDAR 연결 오류"); }
        finally { _lidarButton.Enabled = true; }
    }

    private double ScanFrequency => _scanFrequency.Value / 10.0;

    private async Task ApplyScanFrequencyAsync()
    {
        double target = ScanFrequency;
        _config.Set("connection", "scan_frequency", target);
        _config.Save();
        if (!_lidar.IsRunning)
        {
            _status.Text = $"스캔 속도 설정: {target:F1} Hz ({target * 60:F0} RPM), 연결 후 적용";
            return;
        }
        try
        {
            _status.Text = $"스캔 속도 {target:F1} Hz로 변경 중...";
            double actual = await _lidar.SetScanFrequencyAsync(target);
            _scanFrequency.Value = Math.Clamp((int)Math.Round(actual * 10), _scanFrequency.Minimum, _scanFrequency.Maximum);
            _config.Set("connection", "scan_frequency", actual);
            _config.Save();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "스캔 속도 변경 오류"); }
    }

    private void ToggleAnt()
    {
        if (_ant.IsRunning)
        {
            _ant.Stop();
            _antButton.Text = "ANT 시작";
            _bpm.Text = "-- BPM";
            return;
        }
        _ant.Start(); _antButton.Text = "ANT 중지";
    }

    private void OpenAntScan()
    {
        if (_ant.IsRunning) { MessageBox.Show("현재 ANT 수신을 먼저 중지하세요."); return; }
        var dialog = new Form { Text = "ANT 장비 검색", Width = 420, Height = 340, StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill };
        var select = new Button { Text = "선택 및 저장", Dock = DockStyle.Bottom, Height = 40 };
        dialog.Controls.Add(list); dialog.Controls.Add(select);
        void Found(AntDevice device) => SafeUi(() => { if (!dialog.IsDisposed) list.Items.Add(device); });
        _ant.DeviceFound += Found;
        dialog.FormClosed += (_, _) => { _ant.DeviceFound -= Found; _ant.Stop(); };
        select.Click += (_, _) =>
        {
            if (list.SelectedItem is not AntDevice device) return;
            _ant.SelectedDevice = device;
            _deviceLabel.Text = $"ANT 장비: {device}";
            _config.Set("ant_device", "device_id", device.Id);
            _config.Set("ant_device", "device_type", device.Type);
            _config.Set("ant_device", "transmission_type", device.TransmissionType);
            _config.Save();
            dialog.Close();
        };
        dialog.Show(this);
        _ant.Start(scan: true);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal readonly record struct ScanPreviewSettings(
    int Threshold, int AreaX, int AreaY, int AreaWidth, int AreaHeight,
    int StartAngle, int EndAngle, int Rotation, bool FlipX, bool FlipY,
    int ScreenWidth, int ScreenHeight, int OffsetX, int OffsetY);

internal sealed class ScanView : Control
{
    private readonly double[] _distances = new double[360];
    private IReadOnlyList<TouchContact> _contacts = [];
    private ScanPreviewSettings _settings = new(1000, -300, 330, 600, -300, 90, 270, 90, true, false, 1920, 1080, 0, 0);
    private PreviewMode _mode = PreviewMode.Full;

    public ScanView() { DoubleBuffered = true; }

    public void Configure(ScanPreviewSettings settings)
    {
        _settings = settings;
        Invalidate();
    }

    public void SetPreviewMode(PreviewMode mode)
    {
        _mode = mode;
        if (mode != PreviewMode.Full) Array.Clear(_distances);
        if (mode == PreviewMode.None) _contacts = [];
        Invalidate();
    }

    public void UpdateScan(ScanPoint[] points, IReadOnlyList<TouchContact> contacts, PreviewMode mode)
    {
        if (mode != _mode) SetPreviewMode(mode);
        if (mode == PreviewMode.Full)
            foreach (var point in points) _distances[(int)Math.Round(point.Angle) % 360] = point.Distance;
        _contacts = contacts.ToArray();
        Invalidate();
    }

    private TouchPoint Transform(double distance, double angle)
    {
        double radians = angle * Math.PI / 180;
        double x = distance * Math.Cos(radians), y = distance * Math.Sin(radians);
        (x, y) = ((_settings.Rotation % 360 + 360) % 360) switch
        {
            90 => (y, -x), 180 => (-x, -y), 270 => (-y, x), _ => (x, y)
        };
        if (_settings.FlipX) x = -x;
        if (_settings.FlipY) y = -y;
        return new(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 100 || Height < 100) return;
        var g = e.Graphics;
        if (_mode == PreviewMode.None)
        {
            using var offFont = new Font("Segoe UI", 14, FontStyle.Bold);
            const string message = "프리뷰 없음";
            var size = g.MeasureString(message, offFont);
            g.DrawString(message, offFont, Brushes.SlateGray, (Width - size.Width) / 2, (Height - size.Height) / 2);
            return;
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var s = _settings;
        double radius = Math.Max(s.Threshold, 100);
        double left = Math.Min(-radius, Math.Min(s.AreaX, s.AreaX + s.AreaWidth));
        double right = Math.Max(radius, Math.Max(s.AreaX, s.AreaX + s.AreaWidth));
        double bottom = Math.Min(-radius, Math.Min(s.AreaY, s.AreaY + s.AreaHeight));
        double top = Math.Max(radius, Math.Max(s.AreaY, s.AreaY + s.AreaHeight));
        const float marginX = 50, marginTop = 85, marginBottom = 70;
        float scale = (float)Math.Min((Width - 2 * marginX) / Math.Max(1, right - left),
            (Height - marginTop - marginBottom) / Math.Max(1, top - bottom));
        float centerX = Width / 2f - (float)((left + right) / 2) * scale;
        float centerY = (Height + marginTop - marginBottom) / 2f + (float)((bottom + top) / 2) * scale;
        PointF At(double x, double y) => new(centerX + (float)x * scale, centerY - (float)y * scale);
        PointF Polar(double distance, double angle)
        {
            var point = Transform(distance, angle);
            return At(point.X, point.Y);
        }

        using var gridPen = new Pen(Color.FromArgb(52, 72, 94));
        using var sectorBrush = new SolidBrush(Color.FromArgb(35, 239, 196, 59));
        using var thresholdPen = new Pen(Color.FromArgb(249, 115, 22), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var areaPen = new Pen(Color.FromArgb(74, 222, 128), 2);
        using var areaBrush = new SolidBrush(Color.FromArgb(35, 74, 222, 128));
        using var startPen = new Pen(Color.FromArgb(251, 191, 36), 2);
        using var endPen = new Pen(Color.FromArgb(248, 113, 113), 2);
        using var textBrush = new SolidBrush(Color.FromArgb(225, 235, 245));
        using var smallFont = new Font("Segoe UI", 9);
        using var titleFont = new Font("Segoe UI", 11, FontStyle.Bold);

        int gridStep = radius > 4000 ? 2000 : radius > 1800 ? 1000 : 500;
        for (int r = gridStep; r < radius; r += gridStep)
        {
            float rr = r * scale;
            g.DrawEllipse(gridPen, centerX - rr, centerY - rr, rr * 2, rr * 2);
            g.DrawString($"{r} mm", smallFont, textBrush, centerX + rr + 3, centerY + 3);
        }
        g.DrawLine(gridPen, 0, centerY, Width, centerY);
        g.DrawLine(gridPen, centerX, marginTop, centerX, Height - marginBottom);

        int sweep = (s.EndAngle - s.StartAngle + 360) % 360;
        var sector = new List<PointF> { At(0, 0) };
        for (int step = 0; step <= sweep; step += 3) sector.Add(Polar(radius, s.StartAngle + step));
        sector.Add(Polar(radius, s.EndAngle));
        if (sector.Count >= 3) g.FillPolygon(sectorBrush, sector.ToArray());

        float thresholdRadius = (float)radius * scale;
        g.DrawEllipse(thresholdPen, centerX - thresholdRadius, centerY - thresholdRadius,
            2 * thresholdRadius, 2 * thresholdRadius);
        var start = Polar(radius, s.StartAngle);
        var end = Polar(radius, s.EndAngle);
        g.DrawLine(startPen, At(0, 0), start);
        g.DrawLine(endPen, At(0, 0), end);
        g.DrawString($"시작 {s.StartAngle}°", smallFont, Brushes.Gold,
            Math.Clamp(start.X + 5, 0, Width - 80), Math.Clamp(start.Y - 20, marginTop, Height - 35));
        g.DrawString($"끝 {s.EndAngle}°", smallFont, Brushes.Salmon,
            Math.Clamp(end.X + 5, 0, Width - 80), Math.Clamp(end.Y + 3, marginTop, Height - 25));

        var areaA = At(s.AreaX, s.AreaY);
        var areaB = At(s.AreaX + s.AreaWidth, s.AreaY + s.AreaHeight);
        var area = RectangleF.FromLTRB(Math.Min(areaA.X, areaB.X), Math.Min(areaA.Y, areaB.Y),
            Math.Max(areaA.X, areaB.X), Math.Max(areaA.Y, areaB.Y));
        g.FillRectangle(areaBrush, area);
        g.DrawRectangle(areaPen, area.X, area.Y, area.Width, area.Height);
        g.FillEllipse(Brushes.DodgerBlue, areaA.X - 5, areaA.Y - 5, 10, 10);
        g.DrawString($"기준점 ({s.AreaX}, {s.AreaY})", smallFont, Brushes.LightSkyBlue,
            Math.Clamp(areaA.X + 7, 0, Width - 150), Math.Clamp(areaA.Y - 22, marginTop, Height - 35));

        if (_mode == PreviewMode.Full)
        for (int angle = 0; angle < 360; angle++)
        {
            double distance = _distances[angle];
            if (distance <= 0 || distance > radius * 1.5) continue;
            var point = Polar(distance, angle);
            g.FillEllipse(Brushes.Cyan, point.X - 2, point.Y - 2, 4, 4);
        }
        foreach (var contact in _contacts)
        {
            var hit = contact.Position;
            var point = At(hit.X, hit.Y);
            Brush brush = contact.Gesture switch
            {
                TouchGesture.Drag => Brushes.Magenta,
                _ => Brushes.OrangeRed
            };
            string label = contact.Gesture switch
            {
                TouchGesture.Drag => "드래그",
                _ => "터치"
            };
            g.FillEllipse(brush, point.X - 9, point.Y - 9, 18, 18);
            g.DrawEllipse(Pens.White, point.X - 11, point.Y - 11, 22, 22);
            g.DrawString($"#{contact.Id} {label} ({hit.X:0}, {hit.Y:0})", smallFont, Brushes.White, point.X + 13, point.Y - 22);
        }
        g.FillEllipse(Brushes.White, centerX - 5, centerY - 5, 10, 10);
        g.DrawString($"터치 영역 {Math.Abs(s.AreaWidth)} × {Math.Abs(s.AreaHeight)} mm → 화면 {s.ScreenWidth} × {s.ScreenHeight} px",
            titleFont, textBrush, 15, 14);
        g.DrawString($"거리 {s.Threshold} mm | 각도 {s.StartAngle}°–{s.EndAngle}° | Offset {s.OffsetX}, {s.OffsetY} mm | 그룹 50/200 mm",
            smallFont, textBrush, 15, 42);
        g.DrawString("주황: 터치   자홍: 드래그(50mm)   Up: 미감지 250ms",
            smallFont, textBrush, 15, Height - 28);
    }
}
