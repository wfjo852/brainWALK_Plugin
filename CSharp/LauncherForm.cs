using System.Diagnostics;

namespace BrainWalk;

public sealed class LauncherForm : Form
{
    private readonly AppConfig _config = new();
    private readonly TextBox _gamePath = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(245, 248, 244) };
    private readonly Button _browseButton = new LauncherButton { Text = "찾아보기" };
    private readonly Button _settingsButton = new LauncherButton { Text = "장치 설정" };
    private readonly Button _runButton = new LauncherButton { Text = "게임 시작하기   →", Primary = true };
    private readonly Label _status = new() { ForeColor = LauncherTheme.Muted, BackColor = Color.FromArgb(242, 247, 239) };
    private readonly ToolTip _pathTip = new();

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

        _gamePath.Text = _config.Get("game", "exe_path", "");
        UpdatePathStatus();
        _browseButton.Click += (_, _) => BrowseGamePath();
        _settingsButton.Click += (_, _) => OpenSettings();
        _runButton.Click += async (_, _) => await RunGameAsync();
    }

    private void BuildUi()
    {
        var root = new LauncherBackdrop { Dock = DockStyle.Fill };
        var card = new LauncherCard { Location = new Point(448, 48), Size = new Size(484, 490) };
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
        AddLabel("게임 실행 파일", 36, 178, 412, 24, 10, LauncherTheme.Ink, true);

        var pathBox = new LauncherCard
        {
            Location = new Point(36, 211), Size = new Size(300, 48),
            FillColor = Color.FromArgb(245, 248, 244), BorderColor = Color.FromArgb(225, 232, 221), Radius = 12
        };
        _gamePath.SetBounds(14, 14, 272, 24);
        _gamePath.Font = new Font(Font.FontFamily, 9F);
        _gamePath.ForeColor = LauncherTheme.Ink;
        _gamePath.PlaceholderText = "실행할 게임의 .exe 파일을 선택하세요";
        _gamePath.AccessibleName = "게임 실행 파일 경로";
        pathBox.Controls.Add(_gamePath);
        _browseButton.SetBounds(346, 211, 102, 48);
        _browseButton.TabIndex = 1;
        AddLabel("처음 한 번만 선택하면 다음에도 기억해요.", 36, 270, 412, 22, 9, LauncherTheme.Muted);

        _settingsButton.SetBounds(36, 315, 126, 58);
        _settingsButton.TabIndex = 2;
        _runButton.SetBounds(174, 315, 274, 58);
        _runButton.TabIndex = 3;
        var statusBox = new LauncherCard
        {
            Location = new Point(36, 397), Size = new Size(412, 60),
            FillColor = Color.FromArgb(242, 247, 239), Radius = 14
        };
        _status.SetBounds(16, 10, 380, 42);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font(Font.FontFamily, 9F);
        _status.AccessibleName = "실행 상태";
        statusBox.Controls.Add(_status);
        card.Controls.AddRange([pathBox, _browseButton, _settingsButton, _runButton, statusBox]);
        root.Controls.Add(card);
        Controls.Add(root);
        AcceptButton = _runButton;
        ActiveControl = _browseButton;
    }

    private void UpdatePathStatus()
    {
        _pathTip.SetToolTip(_gamePath, _gamePath.Text);
        _status.Text = File.Exists(_gamePath.Text)
            ? "●  준비 완료 · 게임을 시작할 수 있어요."
            : "●  게임 파일을 선택해 주세요. 장치는 설정에서 확인하세요.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pathTip.Dispose();
        base.Dispose(disposing);
    }

    private void OpenSettings()
    {
        Hide();
        try
        {
            using var mainForm = new MainForm();
            mainForm.ShowDialog(this);
        }
        finally
        {
            Show();
        }
    }

    private void BrowseGamePath()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "게임 실행 파일 선택",
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            // Vista 스타일 공용 대화상자(IFileOpenDialog)는 OneDrive나 끊긴 네트워크 드라이브가
            // 있을 때 쉘 네임스페이스를 열다가 멈추는 경우가 있어, 가벼운 구버전 대화상자를 사용한다.
            AutoUpgradeEnabled = false,
            FileName = File.Exists(_gamePath.Text) ? _gamePath.Text : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _gamePath.Text = dialog.FileName;
        _config.Set("game", "exe_path", dialog.FileName);
        _config.Save();
        UpdatePathStatus();
    }

    private async Task RunGameAsync()
    {
        if (string.IsNullOrWhiteSpace(_gamePath.Text) || !File.Exists(_gamePath.Text))
        {
            MessageBox.Show(this, "먼저 게임 경로를 지정해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            BrowseGamePath();
            if (string.IsNullOrWhiteSpace(_gamePath.Text) || !File.Exists(_gamePath.Text)) return;
        }

        string? selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath) || !File.Exists(selfPath))
        {
            MessageBox.Show(this, "플러그인 실행 파일 경로를 확인할 수 없습니다.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _runButton.Enabled = false;
        _browseButton.Enabled = false;
        _settingsButton.Enabled = false;
        _runButton.Text = "연결하는 중…";
        _status.Text = "플러그인(LiDAR/ANT) 시작 중...";
        try
        {
            using var plugin = Process.Start(new ProcessStartInfo(selfPath, "-console") { UseShellExecute = true });
            if (plugin is null) throw new InvalidOperationException("플러그인 프로세스를 시작하지 못했습니다.");

            // 콘솔 모드는 LiDAR/ANT 연결에 실패하면 바로 종료 코드 1로 끝난다.
            // 잠시 기다려 본 뒤에도 살아있으면 정상 시작으로 간주한다.
            await Task.Delay(3000);
            if (plugin.HasExited)
            {
                _status.Text = $"플러그인 시작 실패 (종료 코드 {plugin.ExitCode})";
                MessageBox.Show(this, "LiDAR/ANT 연결에 실패했습니다. 설정을 확인해주세요.", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _status.Text = "게임 실행 중...";
            Process.Start(new ProcessStartInfo(_gamePath.Text)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_gamePath.Text) ?? ""
            });
            _status.Text = "게임이 실행되었습니다.";
        }
        catch (Exception ex)
        {
            _status.Text = "오류가 발생했습니다.";
            MessageBox.Show(this, ex.Message, "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runButton.Enabled = true;
            _browseButton.Enabled = true;
            _settingsButton.Enabled = true;
            _runButton.Text = "게임 시작하기   →";
        }
    }
}
