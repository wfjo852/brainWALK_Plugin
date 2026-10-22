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
    private readonly CheckBox _drag = new() { Text = "드래그 사용", AutoSize = true, Checked = false };
    private readonly ComboBox _previewMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _bpm = new() { Text = "-- BPM", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
    private readonly Label _deviceLabel = new() { Text = "ANT 장비: --", AutoSize = false, Width = 420, Height = 30, AutoEllipsis = true };
    private readonly Label _status = new() { Text = "준비", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _lidarButton = new LauncherButton { Text = "LiDAR 시작", Width = 180, Height = 42, Primary = true };
    private readonly Button _antButton = new LauncherButton { Text = "ANT 시작", Width = 160, Height = 42, Primary = true };
    private readonly ScanView _view = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 44, 38) };
    private int _previewModeValue = (int)PreviewMode.Full;

    public MainForm()
    {
        Text = "brainWALK — 장치 설정";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10F);
        ForeColor = LauncherTheme.Ink;
        BackColor = SettingsTheme.Canvas;
        ClientSize = new Size(1240, 820);
        MinimumSize = new Size(1080, 690);
        StartPosition = FormStartPosition.CenterParent;
        DoubleBuffered = true;
        using (var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!))
            if (appIcon is not null) Icon = (Icon)appIcon.Clone();
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

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Keep the footer inside the usable desktop, including on smaller displays
        // and when Windows display scaling makes the initial window taller.
        var workspace = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, workspace.Width), Math.Min(MinimumSize.Height, workspace.Height));
        Size = new Size(Math.Min(Width, workspace.Width), Math.Min(Height, workspace.Height));
        Location = new Point(
            Math.Clamp(Left, workspace.Left, workspace.Right - Width),
            Math.Clamp(Top, workspace.Top, workspace.Bottom - Height));
    }

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
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            BackColor = SettingsTheme.Canvas, Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.Controls.Add(new SettingsHeader { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2,
            BackColor = SettingsTheme.Canvas, Margin = new Padding(24, 0, 24, 0),
            SplitterWidth = 20, TabStop = false
        };
        root.Controls.Add(split, 0, 1);
        Controls.Add(root);
        Shown += (_, _) =>
        {
            int scale(int value) => (int)Math.Round(value * DeviceDpi / 96f);
            split.Panel1MinSize = scale(360);
            split.Panel2MinSize = scale(510);
            split.SplitterDistance = Math.Max(split.Panel1MinSize, split.ClientSize.Width - scale(540) - split.SplitterWidth);
        };

        var preview = new LauncherCard { Dock = DockStyle.Fill, Padding = new Padding(16), FillColor = Color.FromArgb(24, 44, 38) };
        var previewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = preview.FillColor };
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var previewHeading = new Panel { Dock = DockStyle.Fill, BackColor = preview.FillColor };
        previewHeading.Controls.Add(new Label { Text = "LIVE PREVIEW", Location = new Point(8, 2), AutoSize = true, ForeColor = Color.FromArgb(174, 204, 161), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        previewHeading.Controls.Add(new Label { Text = "센서와 터치 영역 미리보기", Location = new Point(8, 27), AutoSize = true, ForeColor = Color.White, Font = new Font(Font.FontFamily, 13, FontStyle.Bold) });
        previewLayout.Controls.Add(previewHeading, 0, 0);
        previewLayout.Controls.Add(_view, 0, 1);
        previewLayout.Controls.Add(new Label
        {
            Text = "●  터치     ●  드래그     ·     마우스 제어  Ctrl + Shift + M",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(197, 214, 201), Font = new Font(Font.FontFamily, 9)
        }, 0, 2);
        preview.Controls.Add(previewLayout);
        split.Panel1.Controls.Add(preview);

        var tabs = new SettingsTabs { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(tabs);
        var connection = SettingsTheme.Page(tabs, "장치 연결");
        var touch = SettingsTheme.Page(tabs, "터치 영역");
        var display = SettingsTheme.Page(tabs, "화면 · 제어");

        _scanFrequency.Width = 210;
        _scanFrequencyValue.Width = 64;
        _scanFrequency.BackColor = Color.White;
        _scanFrequency.TickStyle = TickStyle.None;
        _scanFrequencyValue.ForeColor = LauncherTheme.Green;
        _scanFrequencyValue.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        _lidarButton.Click += (_, _) => ToggleLidar();
        SettingsTheme.Section(connection, "01   LiDAR 연결", "센서의 연결 포트와 스캔 속도를 설정하세요.",
            Row("연결 포트", _ports, Button("새로고침", RefreshPorts)),
            Row("통신 속도", _baud, Unit("bps")),
            Row("스캔 속도", _scanFrequency, _scanFrequencyValue),
            _lidarButton);

        _bpm.ForeColor = LauncherTheme.Green;
        _bpm.Margin = new Padding(0, 4, 0, 12);
        _deviceLabel.ForeColor = LauncherTheme.Muted;
        _antButton.Click += (_, _) => ToggleAnt();
        SettingsTheme.Section(connection, "02   ANT+ 심박", "심박 센서를 검색하고 수신 상태를 확인하세요.",
            _deviceLabel, _bpm, SettingsTheme.Actions(_antButton, Button("ANT 장비 검색", OpenAntScan)));

        SettingsTheme.Section(touch, "01   터치 감지", "감지 거리와 유효 포인트 수를 조정하세요.",
            Row("거리 임계값", _threshold, Unit("mm")), Row("최소 포인트", _minPoints, Unit("개")),
            SettingsTheme.Hint("포인트 그룹 간격 50 mm · 최대 지름 200 mm"));
        SettingsTheme.Section(touch, "02   영역 및 위치", "좌표와 크기, 터치 위치 보정을 설정하세요.",
            Row("X / Y", _areaX, Unit("/"), _areaY, Unit("mm")),
            Row("너비 / 높이", _areaWidth, Unit("/"), _areaHeight, Unit("mm")),
            Row("보정 X / Y", _touchOffsetX, Unit("/"), _touchOffsetY, Unit("mm")));
        _flipX.Text = "X축 반전";
        _flipY.Text = "Y축 반전";
        SettingsTheme.Section(touch, "03   각도 및 방향", "센서 설치 방향에 맞춰 감지 범위를 조정하세요.",
            Row("시작 / 끝", _startAngle, Unit("/"), _endAngle, Unit("°")),
            Row("회전", _rotation, Unit("°")), Row("반전", _flipX, _flipY));

        _previewMode.Items.AddRange(["터치 위치만 보기", "전체 프리뷰", "프리뷰 없음"]);
        SettingsTheme.Section(display, "01   프리뷰", "왼쪽 미리보기에 표시할 정보를 선택하세요.", Row("표시 방식", _previewMode));
        SettingsTheme.Section(display, "02   출력 모니터", "터치 입력을 전달할 화면을 선택하세요.",
            Row("모니터", _monitors), Button("모니터 새로고침", RefreshMonitors));
        _mouse.Margin = new Padding(0, 4, 0, 12);
        _drag.Margin = new Padding(0, 0, 0, 12);
        SettingsTheme.Section(display, "03   마우스 제어", "터치로 마우스를 움직이고 드래그할 수 있어요.",
            _mouse, _drag, SettingsTheme.Hint("주황: 터치 · 자홍: 드래그\nCtrl + Shift + M으로 마우스 제어를 켜고 끌 수 있어요."));

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = SettingsTheme.Canvas,
            Padding = new Padding(24, 16, 24, 28), Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 218));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var statusCard = new LauncherCard { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8), FillColor = Color.FromArgb(232, 240, 225), BorderColor = Color.FromArgb(217, 229, 207), Radius = 16, Margin = new Padding(0, 0, 16, 0) };
        _status.BackColor = statusCard.FillColor;
        _status.ForeColor = LauncherTheme.Green;
        statusCard.Controls.Add(_status);
        footer.Controls.Add(statusCard, 0, 0);
        var save = new LauncherButton { Text = "설정 적용 및 저장   ✓", Primary = true, Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = SettingsTheme.Canvas };
        save.Click += (_, _) => SaveSettings();
        footer.Controls.Add(save, 1, 0);
        root.Controls.Add(footer, 0, 2);
        SettingsTheme.StyleInputs(this);
    }

    private static Label Unit(string text) => new() { Text = text, AutoSize = true, ForeColor = LauncherTheme.Muted, Margin = new Padding(2, 8, 6, 0) };
    private static Button Button(string text, Action action)
    {
        var button = new LauncherButton { Text = text, Width = text.Length > 8 ? 174 : 130, Height = 40, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }
    private static Control Row(string label, params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Width = 450, Height = 46, WrapContents = false, Margin = Padding.Empty, BackColor = Color.White };
        panel.Controls.Add(new Label { Text = label, Width = 102, Height = 36, Margin = new Padding(0, 0, 6, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = LauncherTheme.Ink });
        foreach (var control in controls)
        {
            if (control is not System.Windows.Forms.Button && control is not Label) control.Margin = new Padding(0, 5, 6, 0);
            if (control is NumericUpDown or ComboBox) control.AccessibleName = label;
            panel.Controls.Add(control);
        }
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
        _scanFrequencyValue.Text = $"{ScanFrequency:F1} Hz";
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
        _drag.Checked = _config.Get("mouse_control", "drag_enabled", false);
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
        var dialog = new Form
        {
            Text = "brainWALK — ANT 장비 검색", ClientSize = new Size(480, 410),
            MinimumSize = new Size(420, 360), StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96),
            Font = Font, BackColor = SettingsTheme.Canvas, ForeColor = LauncherTheme.Ink,
            Padding = new Padding(24), Icon = Icon
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.Controls.Add(new Label { Text = "심박 센서를 찾고 있어요", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 16, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = "센서를 착용한 뒤, 검색된 장비를 선택해 주세요.", Dock = DockStyle.Fill, ForeColor = LauncherTheme.Muted, Font = new Font(Font.FontFamily, 9) }, 0, 1);
        var list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = LauncherTheme.Ink, IntegralHeight = false };
        var select = new LauncherButton { Text = "선택한 장비 저장   ✓", Primary = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0), BackColor = SettingsTheme.Canvas, Enabled = false };
        list.SelectedIndexChanged += (_, _) => select.Enabled = list.SelectedItem is AntDevice;
        layout.Controls.Add(list, 0, 2);
        layout.Controls.Add(select, 0, 3);
        dialog.Controls.Add(layout);
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

        using var gridPen = new Pen(Color.FromArgb(57, 81, 68));
        using var sectorBrush = new SolidBrush(Color.FromArgb(35, 239, 196, 59));
        using var thresholdPen = new Pen(Color.FromArgb(249, 115, 22), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var areaPen = new Pen(Color.FromArgb(74, 222, 128), 2);
        using var areaBrush = new SolidBrush(Color.FromArgb(35, 74, 222, 128));
        using var startPen = new Pen(Color.FromArgb(251, 191, 36), 2);
        using var endPen = new Pen(Color.FromArgb(248, 113, 113), 2);
        using var textBrush = new SolidBrush(Color.FromArgb(214, 231, 216));
        using var smallFont = new Font("맑은 고딕", 9);
        using var titleFont = new Font("맑은 고딕", 10, FontStyle.Bold);

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
