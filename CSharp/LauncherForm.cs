using System.Diagnostics;

namespace BrainWalk;

public sealed class LauncherForm : Form
{
    private readonly AppConfig _config = new();
    private readonly TextBox _gamePath = new() { ReadOnly = true, Width = 360 };
    private readonly Button _browseButton = new() { Text = "찾아보기", AutoSize = true };
    private readonly Button _settingsButton = new() { Text = "설정하기", Width = 170, Height = 44 };
    private readonly Button _runButton = new() { Text = "게임 실행하기", Width = 170, Height = 44 };
    private readonly Label _status = new() { Text = "준비", AutoSize = true, ForeColor = Color.DimGray };

    public LauncherForm()
    {
        Text = "brainWALK";
        Width = 600; Height = 280;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _gamePath.Text = _config.Get("game", "exe_path", "");
        _browseButton.Click += (_, _) => BrowseGamePath();
        _settingsButton.Click += (_, _) => OpenSettings();
        _runButton.Click += async (_, _) => await RunGameAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 3,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var pathRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        pathRow.Controls.Add(new Label { Text = "게임 경로", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        pathRow.Controls.Add(_gamePath);
        pathRow.Controls.Add(_browseButton);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 16, 0, 0) };
        buttonRow.Controls.Add(_settingsButton);
        buttonRow.Controls.Add(_runButton);

        root.Controls.Add(pathRow);
        root.Controls.Add(buttonRow);
        root.Controls.Add(_status);
        Controls.Add(root);
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
        }
    }
}
