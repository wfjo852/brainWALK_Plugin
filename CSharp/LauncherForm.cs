using System.Diagnostics;

namespace BrainWalk;

public sealed class LauncherForm : Form
{
    private readonly AppConfig _config = new();
    private readonly Button _settingsButton = new LauncherButton { Text = "장치 설정" };
    private readonly Button _runButton = new LauncherButton { Text = "게임 시작하기   →", Primary = true };
    private readonly Label _status = new() { ForeColor = LauncherTheme.Muted, BackColor = Color.FromArgb(242, 247, 239) };
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
    }

    private void BuildUi()
    {
        var root = new LauncherBackdrop { Dock = DockStyle.Fill };
        var card = new LauncherCard { Location = new Point(448, 120), Size = new Size(484, 360) };
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
        _settingsButton.SetBounds(36, 178, 126, 58);
        _settingsButton.TabIndex = 1;
        _runButton.SetBounds(174, 178, 274, 58);
        _runButton.TabIndex = 2;
        var statusBox = new LauncherCard
        {
            Location = new Point(36, 260), Size = new Size(412, 60),
            FillColor = Color.FromArgb(242, 247, 239), Radius = 14
        };
        _status.SetBounds(16, 10, 380, 42);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = new Font(Font.FontFamily, 9F);
        _status.AccessibleName = "실행 상태";
        statusBox.Controls.Add(_status);
        card.Controls.AddRange([_settingsButton, _runButton, statusBox]);
        root.Controls.Add(card);
        Controls.Add(root);
        AcceptButton = _runButton;
        ActiveControl = _runButton;
    }

    // 게임 실행 파일은 장치 설정(MainForm)의 '설정' 탭에서 지정하고 저장한다.
    private string GamePath => _config.Get("game", "exe_path", "");

    private void UpdatePathStatus()
    {
        _status.Text = File.Exists(GamePath)
            ? $"●  준비 완료 · {Path.GetFileName(GamePath)}"
            : "●  장치 설정 > 설정 탭에서 게임 실행 파일을 선택해 주세요.";
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
            _settingsButton.Enabled = true;
            _runButton.Text = "게임 시작하기   →";
        }
    }
}
