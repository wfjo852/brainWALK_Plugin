using System.Diagnostics;
using System.Text.Json;

namespace BrainWalk;

internal sealed record GuestRecord(string GuestId, string Name, string Birthday, int RestingHr, DateTime CreatedAt);

// 이 PC에서 만든 게스트 목록. 실행 파일 옆 guest_history.json에 저장한다.
internal static class GuestHistory
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "guest_history.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<GuestRecord> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<GuestRecord>>(File.ReadAllText(FilePath), Options) ?? []
                : [];
        }
        catch { return []; }
    }

    public static void Add(GuestRecord record)
    {
        var records = Load();
        records.Add(record);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(records, Options));
    }
}

// 로컬에서 만든 게스트 목록을 보여주고, 선택한 게스트의 결과 페이지를 브라우저로 연다.
internal sealed class GuestHistoryForm : Form
{
    private readonly string _serverUrl;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, BorderStyle = BorderStyle.FixedSingle, GridLines = false,
        BackColor = Color.White, ForeColor = LauncherTheme.Ink
    };
    private readonly Button _openButton = new LauncherButton { Text = "결과 페이지 보기   →", Primary = true, Dock = DockStyle.Fill, Enabled = false };

    public GuestHistoryForm(string serverUrl, Icon? icon)
    {
        _serverUrl = serverUrl;
        Text = "brainWALK — 결과 보기";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10F);
        ForeColor = LauncherTheme.Ink;
        BackColor = SettingsTheme.Canvas;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(24);
        Icon = icon;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = BackColor };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.Controls.Add(new Label { Text = "만든 게스트 목록", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 16, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = $"게스트를 선택하고 '결과 페이지 보기'를 누르세요.  ·  서버: {serverUrl}",
            Dock = DockStyle.Fill, ForeColor = LauncherTheme.Muted, Font = new Font(Font.FontFamily, 9), AutoEllipsis = true
        }, 0, 1);

        _list.Columns.Add("만든 시각", 150);
        _list.Columns.Add("이름", 120);
        _list.Columns.Add("생년월일", 120);
        _list.Columns.Add("안정시 심박수", 110, HorizontalAlignment.Center);
        _list.Columns.Add("게스트 ID", 140, HorizontalAlignment.Center);
        foreach (var guest in GuestHistory.Load().OrderByDescending(g => g.CreatedAt))
        {
            _list.Items.Add(new ListViewItem([
                guest.CreatedAt.ToString("yyyy-MM-dd HH:mm"), guest.Name, guest.Birthday,
                $"{guest.RestingHr} BPM", guest.GuestId
            ]) { Tag = guest });
        }
        layout.Controls.Add(_list, 0, 2);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 16, 0, 0), Margin = Padding.Empty, BackColor = BackColor };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var empty = new Label
        {
            Text = _list.Items.Count == 0 ? "아직 이 PC에서 만든 게스트가 없어요." : $"총 {_list.Items.Count}명",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = LauncherTheme.Muted
        };
        var close = new LauncherButton { Text = "닫기", Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0), BackColor = BackColor };
        close.Click += (_, _) => Close();
        _openButton.BackColor = BackColor;
        _openButton.Margin = Padding.Empty;
        actions.Controls.Add(empty, 0, 0);
        actions.Controls.Add(_openButton, 1, 0);
        actions.Controls.Add(close, 2, 0);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);

        _list.SelectedIndexChanged += (_, _) => _openButton.Enabled = _list.SelectedItems.Count > 0;
        _list.ItemActivate += (_, _) => OpenSelected();
        _openButton.Click += (_, _) => OpenSelected();
        AcceptButton = _openButton;
        CancelButton = close;
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not GuestRecord guest) return;
        var url = GuestApi.ResultPageUrl(_serverUrl, guest.GuestId);
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"브라우저를 열지 못했습니다: {ex.Message}\n{url}", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
