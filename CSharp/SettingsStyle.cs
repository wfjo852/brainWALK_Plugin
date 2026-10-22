using System.Drawing.Drawing2D;

namespace BrainWalk;

internal static class SettingsTheme
{
    public static readonly Color Canvas = Color.FromArgb(244, 247, 238);

    public static Panel Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { BackColor = Canvas, Padding = new Padding(0, 10, 0, 0) };
        var flow = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true, BackColor = Canvas, Padding = new Padding(0, 0, 4, 4)
        };
        // Reserve the scrollbar gutter so cards never introduce horizontal scrolling.
        void LayoutCards()
        {
            int width = Math.Max(1, flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            int top = flow.AutoScrollPosition.Y;
            foreach (Control card in flow.Controls)
            {
                card.SetBounds(0, top, width, card.Height);
                top += card.Height + card.Margin.Bottom;
            }
        }
        flow.ClientSizeChanged += (_, _) => LayoutCards();
        flow.ControlAdded += (_, _) => LayoutCards();
        page.Controls.Add(flow);
        tabs.TabPages.Add(page);
        return flow;
    }

    public static void Section(Panel page, string title, string description, params Control[] controls)
    {
        var card = new LauncherCard
        {
            Width = 494, Radius = 18, Margin = new Padding(0, 0, 0, 12),
            BorderColor = Color.FromArgb(228, 235, 222)
        };
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20, 18, 20, 18), BackColor = Color.Transparent
        };
        var heading = new Label
        {
            Text = title, Height = 29, Width = 450, Margin = Padding.Empty,
            Font = new Font("맑은 고딕", 12, FontStyle.Bold), ForeColor = LauncherTheme.Ink,
            BackColor = Color.White
        };
        var caption = new Label
        {
            Text = description, Height = 26, Width = 450, Margin = new Padding(0, 0, 0, 12),
            Font = new Font("맑은 고딕", 9), ForeColor = LauncherTheme.Muted, BackColor = Color.White
        };
        body.Controls.Add(heading);
        body.Controls.Add(caption);
        int height = body.Padding.Vertical + heading.Height + caption.Height + caption.Margin.Vertical;
        foreach (var control in controls)
        {
            control.BackColor = Color.White;
            if (control.AutoSize) control.Size = control.GetPreferredSize(Size.Empty);
            body.Controls.Add(control);
            height += control.Height + control.Margin.Vertical;
        }
        card.Height = height;
        card.Controls.Add(body);
        body.ClientSizeChanged += (_, _) =>
        {
            int width = Math.Max(1, body.ClientSize.Width - body.Padding.Horizontal);
            heading.Width = caption.Width = width;
            foreach (Control control in body.Controls)
                if (control is FlowLayoutPanel || control is Label { AutoSize: false }) control.Width = width;
        };
        page.Controls.Add(card);
    }

    public static Label Hint(string text) => new()
    {
        Text = text, Width = 450, Height = text.Contains('\n') ? 46 : 28,
        Font = new Font("맑은 고딕", 9), ForeColor = LauncherTheme.Muted,
        Margin = new Padding(0, 4, 0, 0)
    };

    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Width = 450, Height = 44, WrapContents = false, Margin = Padding.Empty };
        foreach (var control in controls) row.Controls.Add(control);
        return row;
    }

    public static void StyleInputs(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Color.FromArgb(241, 246, 236);
                combo.ForeColor = LauncherTheme.Ink;
                combo.IntegralHeight = false;
                combo.DropDownHeight = 240;
            }
            else if (control is NumericUpDown number)
            {
                number.BorderStyle = BorderStyle.FixedSingle;
                number.BackColor = Color.FromArgb(248, 250, 245);
                number.ForeColor = LauncherTheme.Ink;
                number.TextAlign = HorizontalAlignment.Center;
            }
            else if (control is CheckBox check)
            {
                check.ForeColor = LauncherTheme.Ink;
                check.Cursor = Cursors.Hand;
            }
            StyleInputs(control);
        }
    }
}

internal sealed class SettingsTabs : TabControl
{
    public SettingsTabs()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(158, 44);
        Padding = new Point(12, 8);
        Font = new Font("맑은 고딕", 10, FontStyle.Bold);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool selected = e.Index == SelectedIndex;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var backdrop = new SolidBrush(SettingsTheme.Canvas);
        e.Graphics.FillRectangle(backdrop, e.Bounds);
        var rect = Rectangle.Inflate(e.Bounds, -3, -3);
        using var path = LauncherTheme.Rounded(rect, 10 * DeviceDpi / 96f);
        using var fill = new SolidBrush(selected ? LauncherTheme.Green : Color.FromArgb(231, 238, 223));
        e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, rect,
            selected ? Color.White : LauncherTheme.Green, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -5, -5), Color.White, LauncherTheme.Green);
    }
}

internal sealed class SettingsHeader : Control
{
    private readonly Image _logo;

    public SettingsHeader()
    {
        DoubleBuffered = true;
        using var stream = typeof(SettingsHeader).Assembly.GetManifestResourceStream("BrainWalk.RunIcon.png")
            ?? throw new InvalidOperationException("런처 로고를 찾을 수 없습니다.");
        using var source = Image.FromStream(stream);
        _logo = new Bitmap(source);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        float width = Width / scale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var background = new LinearGradientBrush(new PointF(0, 0), new PointF(width, 100),
            Color.FromArgb(233, 245, 236), SettingsTheme.Canvas);
        g.FillRectangle(background, 0, 0, width, 100);
        using var decoration = new SolidBrush(Color.FromArgb(225, 236, 210));
        g.FillEllipse(decoration, width - 270, -110, 320, 190);
        using var decoration2 = new SolidBrush(Color.FromArgb(235, 242, 222));
        g.FillEllipse(decoration2, width - 420, -140, 260, 200);
        g.DrawImage(_logo, 22, 8, 80, 80);
        using var titleFont = new Font("맑은 고딕", 19 * 96f / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font("맑은 고딕", 9 * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(LauncherTheme.Ink);
        using var muted = new SolidBrush(LauncherTheme.Muted);
        g.DrawString("장치 설정", titleFont, ink, 118, 19);
        g.DrawString("산책을 시작하기 전, 센서와 터치 환경을 준비해요.", captionFont, muted, 121, 57);
        using var brandFont = new Font("Segoe UI", 10 * 96f / 72f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var green = new SolidBrush(LauncherTheme.Green);
        g.DrawString("brainWALK  /  DEVICE SETTINGS", brandFont, green, width - 290, 38);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _logo.Dispose();
        base.Dispose(disposing);
    }
}
