using System.Diagnostics;

namespace BrainWalk;

public sealed class LauncherForm : Form
{
    private readonly AppConfig _config = new();
    private readonly Button _settingsButton = new LauncherButton { Text = "장치 설정" };
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
        _runButton.Click += async (_, _) => await RunGameAsync();
        _createGuestButton.Click += async (_, _) => await CreateGuestAsync();
        _resetGuestButton.Click += (_, _) => ShowGuestInput(clear: true);
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
        AddLabel("LET’S WALK TOGETHER", 36, 32, 412, 24, 9, LauncherTheme.Green, true);
        AddLabel("오늘도, 가볍게 한 걸음.", 32, 68, 424, 46, 22, LauncherTheme.Ink, true);
        AddLabel("게임과 함께 즐거운 산책을 시작해 보세요.", 36, 121, 412, 28, 10, LauncherTheme.Muted);
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
        _guestInput.Bounds = _guestResult.Bounds = bounds;
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

        card.Controls.AddRange([_guestInput, _guestResult]);
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
        _guestInput.Visible = true;
        AcceptButton = _createGuestButton;
        _guestName.Focus();
        UpdatePathStatus();
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
            _guestIdxLabel.Text = _guestIdx;
            _guestInfoLabel.Text = $"{name} · {birthday:yyyy-MM-dd} · 안정시 {restingHr} BPM";
            _guestInput.Visible = false;
            _guestResult.Visible = true;
            AcceptButton = _runButton;
            _runButton.Focus();
            _status.Text = $"●  게스트 번호 {_guestIdx} 생성 완료 · 게임 시작 시 클립보드로 복사돼요.";
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
                ? "●  이름·생년월일·안정시 심박수를 입력하고 아이디를 생성해 주세요."
                : $"●  준비 완료 · 게스트 번호 {_guestIdx} · {Path.GetFileName(GamePath)}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pluginProcess?.Dispose();
        base.Dispose(disposing);
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

    private static async Task<int> KillExistingPluginsAsync()
    {
        int selfId = Environment.ProcessId;
        var targets = PluginProcessNames
            .SelectMany(Process.GetProcessesByName)
            .Where(p => p.Id != selfId)
            .ToList();

        int killed = 0;
        foreach (var process in targets)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
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
            MessageBox.Show(this, "먼저 이름·생년월일·안정시 심박수를 입력하고 '아이디 생성'을 눌러주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _guestName.Focus();
            return;
        }
        try
        {
            // 게임 로그인 화면에 붙여넣을 수 있도록 게스트 번호를 클립보드에 복사한다.
            Clipboard.SetDataObject(_guestIdx, copy: true, retryTimes: 5, retryDelay: 100);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"게스트 번호를 클립보드에 복사하지 못했습니다: {ex.Message}\n게스트 번호: {_guestIdx}", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            _pluginProcess = Process.Start(new ProcessStartInfo(selfPath, "-console") { UseShellExecute = true });
            if (_pluginProcess is null) throw new InvalidOperationException("플러그인 프로세스를 시작하지 못했습니다.");

            // 콘솔 모드는 LiDAR/ANT 연결에 실패하면 바로 종료 코드 1로 끝난다.
            // 잠시 기다려 본 뒤에도 살아있으면 정상 시작으로 간주한다.
            await Task.Delay(3000);
            if (_pluginProcess.HasExited)
            {
                _status.Text = $"플러그인 시작 실패 (종료 코드 {_pluginProcess.ExitCode})";
                MessageBox.Show(this, "LiDAR/ANT 연결에 실패했습니다. 설정을 확인해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _pluginProcess.Dispose();
                _pluginProcess = null;
                return;
            }

            _status.Text = "게임 실행 중...";
            Process.Start(new ProcessStartInfo(gamePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(gamePath) ?? ""
            });
            _status.Text = $"게임이 실행되었습니다. 게스트 번호 {_guestIdx}가 클립보드에 복사돼 있어요.";
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
