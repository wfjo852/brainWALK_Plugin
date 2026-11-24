using System.Diagnostics;

namespace BrainWalk;

public sealed class LauncherForm : Form
{
    private readonly AppConfig _config = new();
    private readonly Button _settingsButton = new LauncherButton { Text = "장치 설정" };
    private readonly Button _historyButton = new LauncherButton { Text = "결과 보기", AccessibleName = "만든 게스트 결과 보기" };
    private readonly Button _runButton = new LauncherButton { Text = "게임 시작하기   →", Primary = true };
    private readonly Label _status = new() { ForeColor = LauncherTheme.Muted, BackColor = Color.FromArgb(242, 247, 239) };

    // 게스트 아이디 생성 영역: 입력 화면(_guestInput)과 생성 결과 화면(_guestResult)을 같은 자리에서 전환한다.
    private readonly Panel _guestInput = new() { BackColor = Color.White };
    private readonly Panel _guestResult = new() { BackColor = Color.White, Visible = false };
    private readonly TextBox _guestName = new() { BorderStyle = BorderStyle.None, PlaceholderText = "홍길동", AccessibleName = "이름" };
    private readonly MaskedTextBox _guestBirthday = new() { Mask = "0000-00-00", BorderStyle = BorderStyle.None, PromptChar = '_', AccessibleName = "생년월일" };
    private const string DefaultRestingHr = "70";
    private readonly TextBox _guestRestingHr = new() { BorderStyle = BorderStyle.None, MaxLength = 3, Text = DefaultRestingHr, AccessibleName = "안정시 심박수 (BPM)" };
    private readonly Button _createGuestButton = new LauncherButton { Text = "아이디 생성" };
    private readonly Label _guestIdxLabel = new() { ForeColor = LauncherTheme.Green, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "게스트 번호" };
    private readonly Label _guestInfoLabel = new() { ForeColor = LauncherTheme.Muted, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _resetGuestButton = new LauncherButton { Text = "다시 만들기" };
    // 이미 발급받은 게스트 번호를 아는 경우 직접 입력하는 화면.
    private readonly Panel _guestManual = new() { BackColor = Color.White, Visible = false };
    private readonly TextBox _manualGuestId = new() { BorderStyle = BorderStyle.None, PlaceholderText = "게스트 번호", AccessibleName = "게스트 번호 직접 입력" };
    private readonly Button _useGuestIdButton = new LauncherButton { Text = "이 번호로 시작" };
    private readonly LinkLabel _modeLink = new() { Text = "번호 직접 입력", TextAlign = ContentAlignment.MiddleRight, BackColor = Color.White };
    private string? _guestIdx;

    private Process? _pluginProcess;

    public LauncherForm()
    {
        Text = "brainWALK";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 600);
        Font = new Font("맑은 고딕", 10F);
        BackColor = Color.FromArgb(243, 248, 238);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        using (var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!))
            if (appIcon is not null) Icon = (Icon)appIcon.Clone();

        BuildUi();

        UpdatePathStatus();
        _settingsButton.Click += (_, _) => OpenSettings();
        _historyButton.Click += (_, _) => OpenHistory();
        _runButton.Click += async (_, _) => await RunGameAsync();
        _createGuestButton.Click += async (_, _) => await CreateGuestAsync();
        _resetGuestButton.Click += (_, _) => ShowGuestInput(clear: true);
        _useGuestIdButton.Click += (_, _) => UseManualGuestId();
        _modeLink.LinkClicked += (_, _) =>
        {
            if (_guestManual.Visible) ShowGuestInput(clear: false);
            else ShowManualInput();
        };
    }

    private void BuildUi()
    {
        var root = new LauncherBackdrop { Dock = DockStyle.Fill };
        var card = new LauncherCard { Location = new Point(448, 14), Size = new Size(484, 534) };
        void AddLabel(string text, int x, int y, int width, int height, float size, Color color, bool bold = false)
        {
            card.Controls.Add(new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color, BackColor = Color.White
            });
        }
        AddLabel("LET’S WALK TOGETHER", 36, 32, 300, 24, 9, LauncherTheme.Green, true);
        _historyButton.SetBounds(348, 24, 100, 36);
        _historyButton.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _historyButton.TabIndex = 7;
        card.Controls.Add(_historyButton);
        AddLabel("오늘도, 가볍게 한 걸음.", 32, 68, 424, 46, 22, LauncherTheme.Ink, true);
        AddLabel("게임과 함께 즐거운 산책을 시작해 보세요.", 36, 121, 290, 28, 10, LauncherTheme.Muted);
        _modeLink.SetBounds(328, 121, 120, 28);
        _modeLink.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
        _modeLink.LinkColor = _modeLink.ActiveLinkColor = LauncherTheme.Green;
        _modeLink.TabIndex = 8;
        card.Controls.Add(_modeLink);
        BuildGuestUi(card, new Rectangle(36, 160, 412, 206));

        _settingsButton.SetBounds(36, 382, 126, 56);
        _settingsButton.TabIndex = 5;
        _runButton.SetBounds(174, 382, 274, 56);
        _runButton.TabIndex = 6;
        var statusBox = new LauncherCard
        {
            Location = new Point(36, 454), Size = new Size(412, 56),
            FillColor = Color.FromArgb(242, 247, 239), Radius = 14
        };
        _status.SetBounds(16, 8, 380, 40);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font(Font.FontFamily, 9F);
        _status.AccessibleName = "실행 상태";
        statusBox.Controls.Add(_status);
        card.Controls.AddRange([_settingsButton, _runButton, statusBox]);
        root.Controls.Add(card);
        Controls.Add(root);
        AcceptButton = _createGuestButton;
        ActiveControl = _guestName;
    }

    private void BuildGuestUi(Control card, Rectangle bounds)
    {
        _guestInput.Bounds = _guestResult.Bounds = _guestManual.Bounds = bounds;
        var inputFont = new Font(Font.FontFamily, 12F);

        Label FieldLabel(string text, int y) => new()
        {
            Text = text, Location = new Point(0, y), Size = new Size(110, 44),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = LauncherTheme.Ink, BackColor = Color.White,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold)
        };
        // 디자인에 맞춘 둥근 입력칸: 연한 테두리의 라운드 박스 안에 테두리 없는 입력 컨트롤을 넣는다.
        var fieldFill = Color.FromArgb(247, 250, 245);
        var fieldBorder = Color.FromArgb(226, 233, 221);
        const int fieldX = 116, fieldWidth = 296, fieldHeight = 44, rowGap = 52;
        Control Field(Control input, int y, string? suffix, int tabIndex)
        {
            var box = new LauncherCard
            {
                Location = new Point(fieldX, y), Size = new Size(fieldWidth, fieldHeight),
                FillColor = fieldFill, BorderColor = fieldBorder, Radius = 12, Cursor = Cursors.IBeam
            };
            int suffixWidth = 0;
            if (suffix is not null)
            {
                suffixWidth = 104;
                var suffixLabel = new Label
                {
                    Text = suffix, Bounds = new Rectangle(fieldWidth - suffixWidth - 14, 4, suffixWidth, fieldHeight - 8),
                    TextAlign = ContentAlignment.MiddleRight, ForeColor = LauncherTheme.Muted, BackColor = fieldFill,
                    Font = new Font(Font.FontFamily, 9F), Cursor = Cursors.IBeam
                };
                suffixLabel.Click += (_, _) => input.Focus();
                box.Controls.Add(suffixLabel);
            }
            input.Font = inputFont;
            input.ForeColor = LauncherTheme.Ink;
            input.BackColor = fieldFill;
            input.TabIndex = tabIndex;
            input.Width = fieldWidth - 32 - suffixWidth;
            input.Location = new Point(16, (fieldHeight - input.Height) / 2);
            box.Controls.Add(input);
            box.Click += (_, _) => input.Focus();
            return box;
        }
        _guestRestingHr.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
        _createGuestButton.SetBounds(0, rowGap * 3 + 2, 412, 46);
        _createGuestButton.TabIndex = 4;
        _guestInput.Controls.AddRange([
            FieldLabel("이름", 0), Field(_guestName, 0, null, 1),
            FieldLabel("생년월일", rowGap), Field(_guestBirthday, rowGap, "예) 1960-01-31", 2),
            FieldLabel("안정시 심박수", rowGap * 2), Field(_guestRestingHr, rowGap * 2, "BPM", 3),
            _createGuestButton]);

        var resultBox = new LauncherCard
        {
            Location = new Point(0, 0), Size = new Size(412, 144),
            FillColor = Color.FromArgb(242, 247, 239), BorderColor = fieldBorder, Radius = 14
        };
        var resultCaption = new Label
        {
            Text = "게임에 로그인할 게스트 번호", Location = new Point(16, 16), Size = new Size(380, 22),
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = LauncherTheme.Muted, BackColor = resultBox.FillColor,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold)
        };
        _guestIdxLabel.SetBounds(16, 42, 380, 58);
        _guestIdxLabel.BackColor = resultBox.FillColor;
        _guestIdxLabel.Font = new Font(Font.FontFamily, 28F, FontStyle.Bold);
        _guestInfoLabel.SetBounds(16, 104, 380, 24);
        _guestInfoLabel.BackColor = resultBox.FillColor;
        _guestInfoLabel.Font = new Font(Font.FontFamily, 9F);
        resultBox.Controls.AddRange([resultCaption, _guestIdxLabel, _guestInfoLabel]);
        _resetGuestButton.SetBounds(0, rowGap * 3 + 2, 412, 46);
        _guestResult.Controls.AddRange([resultBox, _resetGuestButton]);

        _manualGuestId.KeyPress += (_, e) => e.Handled = char.IsWhiteSpace(e.KeyChar);
        var manualHint = new Label
        {
            Text = "이미 발급받은 게스트 번호가 있으면 입력해 주세요.", Location = new Point(0, rowGap), Size = new Size(412, 44),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = LauncherTheme.Muted, BackColor = Color.White,
            Font = new Font(Font.FontFamily, 9F)
        };
        _useGuestIdButton.SetBounds(0, rowGap * 3 + 2, 412, 46);
        _useGuestIdButton.TabIndex = 2;
        _guestManual.Controls.AddRange([
            FieldLabel("게스트 번호", 0), Field(_manualGuestId, 0, null, 1),
            manualHint, _useGuestIdButton]);

        card.Controls.AddRange([_guestInput, _guestResult, _guestManual]);
    }

    private void ShowGuestInput(bool clear)
    {
        _guestIdx = null;
        if (clear)
        {
            _guestName.Clear();
            _guestBirthday.Clear();
            _guestRestingHr.Text = DefaultRestingHr;
        }
        _guestResult.Visible = false;
        _guestManual.Visible = false;
        _guestInput.Visible = true;
        _modeLink.Text = "번호 직접 입력";
        AcceptButton = _createGuestButton;
        _guestName.Focus();
        UpdatePathStatus();
    }

    private void ShowManualInput()
    {
        _guestIdx = null;
        _guestInput.Visible = false;
        _guestResult.Visible = false;
        _guestManual.Visible = true;
        _modeLink.Text = "새 아이디 만들기";
        AcceptButton = _useGuestIdButton;
        _manualGuestId.Focus();
        _manualGuestId.SelectAll();
        UpdatePathStatus();
    }

    private void UseManualGuestId()
    {
        string guestId = _manualGuestId.Text.Trim();
        if (guestId.Length == 0)
        {
            MessageBox.Show(this, "게스트 번호를 입력해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _manualGuestId.Focus();
            return;
        }

        // 이 PC에서 만든 번호라면 저장된 정보(이름 등)를 함께 보여준다.
        GuestRecord? known = null;
        try { known = GuestHistory.Load().LastOrDefault(r => string.Equals(r.GuestId, guestId, StringComparison.OrdinalIgnoreCase)); }
        catch { /* 기록을 읽지 못해도 직접 입력한 번호는 그대로 사용한다. */ }

        _guestIdx = guestId;
        _guestIdxLabel.Text = guestId;
        _guestInfoLabel.Text = known is null
            ? "직접 입력한 번호"
            : $"{known.Name} · {known.Birthday} · 안정시 {known.RestingHr} BPM";
        _guestManual.Visible = false;
        _guestResult.Visible = true;
        _modeLink.Text = "번호 직접 입력";
        AcceptButton = _runButton;
        _runButton.Focus();
        _status.Text = $"●  게스트 번호 {_guestIdx} 사용 · 게임 시작하기를 눌러주세요.";
    }

    private async Task CreateGuestAsync()
    {
        string name = _guestName.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "이름을 입력해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _guestName.Focus();
            return;
        }
        if (!DateTime.TryParseExact(_guestBirthday.Text, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var birthday)
            || birthday.Year < 1900 || birthday > DateTime.Today)
        {
            MessageBox.Show(this, "생년월일을 YYYY-MM-DD 형식으로 정확히 입력해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _guestBirthday.Focus();
            return;
        }
        if (!int.TryParse(_guestRestingHr.Text, out int restingHr) || restingHr is < 30 or > 220)
        {
            MessageBox.Show(this, "안정시 심박수를 30~220 사이의 숫자로 입력해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _guestRestingHr.Focus();
            return;
        }

        _createGuestButton.Enabled = false;
        _runButton.Enabled = false;
        _createGuestButton.Text = "생성하는 중…";
        _status.Text = "게스트 아이디를 만드는 중...";
        try
        {
            string serverUrl = _config.Get("server", "url", GuestApi.DefaultServerUrl);
            _guestIdx = await GuestApi.CreateGuestAsync(serverUrl, name, birthday, restingHr);
            try
            {
                GuestHistory.Add(new GuestRecord(_guestIdx, name, birthday.ToString("yyyy-MM-dd"), restingHr, DateTime.Now));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"게스트 목록을 저장하지 못했습니다: {ex.Message}", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _guestIdxLabel.Text = _guestIdx;
            _guestInfoLabel.Text = $"{name} · {birthday:yyyy-MM-dd} · 안정시 {restingHr} BPM";
            _guestInput.Visible = false;
            _guestResult.Visible = true;
            AcceptButton = _runButton;
            _runButton.Focus();
            _status.Text = $"●  게스트 번호 {_guestIdx} 생성 완료 · 게임 시작하기를 눌러주세요.";
        }
        catch (Exception ex)
        {
            _guestIdx = null;
            _status.Text = "게스트 아이디 생성에 실패했습니다.";
            string reason = ex is TaskCanceledException ? "서버 응답 시간이 초과되었습니다." : ex.Message;
            MessageBox.Show(this, $"게스트 아이디를 만들지 못했습니다.\n{reason}", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _createGuestButton.Enabled = true;
            _runButton.Enabled = true;
            _createGuestButton.Text = "아이디 생성";
        }
    }

    // 게임 실행 파일은 장치 설정(MainForm)의 '설정' 탭에서 지정하고 저장한다.
    private string GamePath => _config.Get("game", "exe_path", "");

    private void UpdatePathStatus()
    {
        _status.Text = !File.Exists(GamePath)
            ? "●  장치 설정 > 설정 탭에서 게임 실행 파일을 선택해 주세요."
            : _guestIdx is null
                ? _guestManual.Visible
                    ? "●  알고 있는 게스트 번호를 입력해 주세요."
                    : "●  이름·생년월일·안정시 심박수를 입력하고 아이디를 생성해 주세요."
                : $"●  준비 완료 · 게스트 번호 {_guestIdx} · {Path.GetFileName(GamePath)}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pluginProcess?.Dispose();
        base.Dispose(disposing);
    }

    private void OpenHistory()
    {
        // 결과 페이지 주소는 설정에 저장된 서버 URL을 기준으로 만든다.
        _config.Reload();
        using var form = new GuestHistoryForm(_config.Get("server", "url", GuestApi.DefaultServerUrl), Icon);
        form.ShowDialog(this);
    }

    private void OpenSettings(bool focusGamePath = false)
    {
        Hide();
        try
        {
            using var mainForm = new MainForm(focusGamePath);
            mainForm.ShowDialog(this);
        }
        finally
        {
            // 설정 화면에서 바뀐 내용(게임 경로 등)을 다시 읽어온다.
            _config.Reload();
            UpdatePathStatus();
            Show();
        }
    }

    // LiDAR 시리얼 포트와 ANT USB 동글은 한 프로세스만 점유할 수 있으므로,
    // 이 런처가 띄운 것뿐 아니라 이전 실행에서 남은 콘솔 플러그인(brainWALK-run -console),
    // 다른 런처 인스턴스까지 모두 종료한다.
    private static readonly string[] PluginProcessNames = ["brainWALK-run"];

    private static Task<int> KillExistingPluginsAsync() =>
        KillProcessesAsync(PluginProcessNames.SelectMany(Process.GetProcessesByName));

    // 이미 실행 중인 게임을 종료한다. 같은 이름의 다른 프로그램을 잘못 끄지 않도록,
    // 실행 경로를 읽을 수 있으면 설정된 게임 경로와 같은 것만 대상으로 한다.
    private static Task<int> KillRunningGameAsync(string gamePath)
    {
        string fullPath = Path.GetFullPath(gamePath);
        var targets = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(gamePath)))
        {
            string? path = TryGetProcessPath(process);
            if (path is null || string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase))
                targets.Add(process);
            else
                process.Dispose();
        }
        return KillProcessesAsync(targets);
    }

    // 런처는 x86이라 64비트 게임의 MainModule은 읽을 수 없으므로 QueryFullProcessImageName을 쓴다.
    private static string? TryGetProcessPath(Process process)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder exeName, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static async Task<int> KillProcessesAsync(IEnumerable<Process> processes)
    {
        int selfId = Environment.ProcessId;
        var targets = processes.ToList();

        int killed = 0;
        foreach (var process in targets)
        {
            using (process)
            {
                try
                {
                    if (process.Id == selfId || process.HasExited) continue;
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                    killed++;
                }
                catch { /* 이미 종료되었거나, 접근 권한이 없거나, 종료 대기 시간 초과 */ }
            }
        }
        return killed;
    }

    private static async Task<PluginStartResult?> WaitForPluginResultAsync(Process plugin, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (PluginStartResult.TryRead() is { } result) return result;
            if (plugin.HasExited) return PluginStartResult.TryRead();
            await Task.Delay(200);
        }
        return PluginStartResult.TryRead();
    }

    private async Task RunGameAsync()
    {
        if (!File.Exists(GamePath))
        {
            MessageBox.Show(this, "먼저 장치 설정의 '설정' 탭에서 게임 실행 파일을 지정해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenSettings(focusGamePath: true);
            if (!File.Exists(GamePath)) return;
        }
        string gamePath = GamePath;

        if (_guestIdx is null)
        {
            if (_guestManual.Visible)
            {
                // 번호만 입력하고 바로 '게임 시작하기'를 누른 경우도 받아준다.
                UseManualGuestId();
                if (_guestIdx is null) return;
            }
            else
            {
                MessageBox.Show(this, "먼저 아이디를 생성하거나, '번호 직접 입력'으로 게스트 번호를 입력해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _guestName.Focus();
                return;
            }
        }

        // Femto 카메라는 필수 장치다. 연결되어 있지 않으면 게임을 시작하지 않는다.
        string femtoVid = _config.Get("femto", "usb_vid", DeviceCheck.DefaultFemtoVendorId);
        if (!DeviceCheck.IsUsbVendorPresent(femtoVid))
        {
            _status.Text = "Femto 카메라가 연결되어 있지 않습니다.";
            MessageBox.Show(this, "Femto 카메라가 연결되어 있지 않습니다.\nUSB 연결과 전원을 확인한 뒤 다시 시도해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath) || !File.Exists(selfPath))
        {
            MessageBox.Show(this, "플러그인 실행 파일 경로를 확인할 수 없습니다.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _runButton.Enabled = false;
        _settingsButton.Enabled = false;
        _runButton.Text = "연결하는 중…";
        try
        {
            _status.Text = "실행 중인 게임을 확인하는 중...";
            if (await KillRunningGameAsync(gamePath) > 0)
            {
                _status.Text = "실행 중이던 게임을 종료했습니다. 새로 시작합니다...";
                await Task.Delay(500);
            }

            _status.Text = "기존 LiDAR/ANT 연결을 확인하는 중...";
            _pluginProcess?.Dispose();
            _pluginProcess = null;
            int killed = await KillExistingPluginsAsync();
            if (killed > 0)
            {
                _status.Text = $"기존 LiDAR/ANT 연결 {killed}개를 종료했습니다. 재시작합니다...";
                // 시리얼 포트(LiDAR)와 ANT USB 동글이 OS에서 완전히 해제될 시간을 준다.
                await Task.Delay(1000);
            }

            _status.Text = "플러그인(LiDAR/ANT) 시작 중...";
            PluginStartResult.Delete();
            _pluginProcess = Process.Start(new ProcessStartInfo(selfPath, "-console") { UseShellExecute = true });
            if (_pluginProcess is null) throw new InvalidOperationException("플러그인 프로세스를 시작하지 못했습니다.");

            // LiDAR와 ANT는 선택 장치다. 콘솔 플러그인이 남기는 상태 파일로 연결 결과를 확인하고,
            // 실패한 장치가 있으면 경고만 띄운 뒤 게임을 계속 진행한다.
            PluginStartResult? result = await WaitForPluginResultAsync(_pluginProcess, TimeSpan.FromSeconds(15));
            var failures = new List<string>();
            if (result is null)
                failures.Add(_pluginProcess.HasExited
                    ? $"플러그인이 종료되었습니다 (종료 코드 {_pluginProcess.ExitCode})."
                    : "LiDAR/ANT 연결 결과를 확인하지 못했습니다.");
            else
            {
                if (result.LidarError is not null) failures.Add($"LiDAR: {result.LidarError}");
                if (result.AntError is not null) failures.Add($"ANT(심박계): {result.AntError}");
            }
            if (_pluginProcess.HasExited)
            {
                _pluginProcess.Dispose();
                _pluginProcess = null;
            }
            if (failures.Count > 0)
            {
                _status.Text = "일부 장치가 연결되지 않았습니다. 게임은 계속 진행합니다.";
                MessageBox.Show(this,
                    "다음 장치가 연결되지 않았습니다.\n\n" + string.Join("\n", failures.Select(f => "• " + f)) +
                    "\n\n해당 기능 없이 게임을 시작합니다.",
                    "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _status.Text = "게임 실행 중...";
            // 게임에 생성된 게스트 번호를 넘겨 해당 게스트로 바로 시작하게 한다: Game.exe -guestid <번호>
            var gameStart = new ProcessStartInfo(gamePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(gamePath) ?? ""
            };
            gameStart.ArgumentList.Add("-guestid");
            gameStart.ArgumentList.Add(_guestIdx);
            Process.Start(gameStart);
            _status.Text = $"게임이 실행되었습니다. (게스트 번호 {_guestIdx})";
        }
        catch (Exception ex)
        {
            _status.Text = "오류가 발생했습니다.";
            MessageBox.Show(this, ex.Message, "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runButton.Enabled = true;
            _settingsButton.Enabled = true;
            _runButton.Text = "게임 시작하기   →";
        }
    }
}
