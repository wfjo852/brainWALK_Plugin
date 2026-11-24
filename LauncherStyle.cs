using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BrainWalk;

internal static class LauncherTheme
{
    public static readonly Color Green = Color.FromArgb(63, 111, 66);
    public static readonly Color Ink = Color.FromArgb(35, 58, 46);
    public static readonly Color Muted = Color.FromArgb(112, 126, 113);

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class LauncherCard : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; init; } = Color.White;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; init; } = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; init; } = 24;

    public LauncherCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        using var path = LauncherTheme.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius * scale);
        using var fill = new SolidBrush(FillColor);
        e.Graphics.FillPath(fill, path);
        if (BorderColor.A > 0)
        {
            using var border = new Pen(BorderColor, scale);
            e.Graphics.DrawPath(border, path);
        }
    }
}

internal sealed class LauncherButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; init; }
    private bool _hovered;
    private bool _pressed;

    public LauncherButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.White;
        Cursor = Cursors.Hand;
        Font = new Font("맑은 고딕", 10, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        var color = Primary
            ? (_pressed ? Color.FromArgb(40, 79, 45) : _hovered ? Color.FromArgb(51, 96, 55) : LauncherTheme.Green)
            : (_pressed ? Color.FromArgb(222, 232, 215) : _hovered ? Color.FromArgb(234, 241, 227) : Color.FromArgb(243, 247, 239));
        if (!Enabled) color = Color.FromArgb(222, 228, 219);
        using var path = LauncherTheme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 12 * scale);
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle,
            !Enabled ? LauncherTheme.Muted : Primary ? Color.White : LauncherTheme.Green,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues)
        {
            using var focusPath = LauncherTheme.Rounded(new RectangleF(5 * scale, 5 * scale, Width - 10 * scale, Height - 10 * scale), 9 * scale);
            using var focusPen = new Pen(Primary ? Color.White : LauncherTheme.Green, scale) { DashStyle = DashStyle.Dot };
            g.DrawPath(focusPen, focusPath);
        }
    }
}

internal sealed class LauncherBackdrop : Panel
{
    private readonly Image _logo;

    public LauncherBackdrop()
    {
        DoubleBuffered = true;
        using var stream = typeof(LauncherBackdrop).Assembly.GetManifestResourceStream("BrainWalk.RunIcon.png")
            ?? throw new InvalidOperationException("런처 로고를 찾을 수 없습니다.");
        using var image = Image.FromStream(stream);
        _logo = new Bitmap(image);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(Width / 980f, Height / 600f);
        using (var sky = new LinearGradientBrush(new Rectangle(0, 0, 980, 600),
                   Color.FromArgb(234, 247, 247), Color.FromArgb(250, 248, 229), 65f))
            g.FillRectangle(sky, 0, 0, 980, 600);

        Ellipse(g, Color.FromArgb(255, 251, 223), 45, 62, 350, 350);
        Ellipse(g, Color.FromArgb(110, Color.White), 11, -155, 460, 390);
        Cloud(g, 339, 105, .65f);
        Cloud(g, 20, 207, .55f);
        Cloud(g, 826, 12, .8f);

        // Layered hills and a winding trail echo the walk in the supplied logo.
        using (var hill = new GraphicsPath())
        {
            hill.AddBezier(-80, 520, 75, 365, 235, 455, 445, 510);
            hill.AddBezier(445, 510, 650, 565, 820, 393, 1060, 450);
            hill.AddLine(1060, 660, -80, 660);
            hill.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(215, 232, 187));
            g.FillPath(fill, hill);
        }
        using (var hill = new GraphicsPath())
        {
            hill.AddBezier(-90, 575, 120, 440, 280, 587, 560, 558);
            hill.AddBezier(560, 558, 760, 538, 920, 485, 1060, 530);
            hill.AddLine(1060, 660, -90, 660);
            hill.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(183, 210, 153));
            g.FillPath(fill, hill);
        }
        using (var trail = new GraphicsPath())
        {
            trail.AddBezier(225, 496, 138, 542, 279, 543, 179, 600);
            trail.AddLine(251, 600, 225, 496);
            trail.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(248, 239, 204));
            g.FillPath(fill, trail);
        }
        Leaf(g, 33, 518, -30);
        Leaf(g, 389, 561, 25);
        Leaf(g, 942, 559, -25);

        // A soft, offset shadow anchors the white action card.
        using (var shadow = LauncherTheme.Rounded(new RectangleF(445, 54, 490, 492), 28))
        using (var fill = new SolidBrush(Color.FromArgb(13, 49, 83, 52))) g.FillPath(fill, shadow);

        g.DrawImage(_logo, new Rectangle(65, 69, 320, 320));
        DrawText(g, "몸도 마음도, 한 걸음 더", 12, FontStyle.Bold, LauncherTheme.Green, new RectangleF(30, 397, 390, 28));
        DrawText(g, "나만의 속도로 즐기는 건강한 산책", 10, FontStyle.Regular, LauncherTheme.Muted, new RectangleF(30, 435, 390, 25));
        DrawText(g, "brainWALK  /  GAME LAUNCHER", 8, FontStyle.Bold, Color.FromArgb(82, 112, 70), new RectangleF(30, 558, 390, 24));
        DrawText(g, "작은 발걸음이 만드는 즐거운 변화", 8, FontStyle.Regular, Color.FromArgb(82, 112, 70), new RectangleF(448, 556, 484, 24));
    }

    private static void DrawText(Graphics g, string text, float size, FontStyle style, Color color, RectangleF bounds)
    {
        using var font = new Font("맑은 고딕", size, style);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static void Ellipse(Graphics g, Color color, float x, float y, float w, float h)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x, y, w, h);
    }

    private static void Cloud(Graphics g, float x, float y, float scale)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(scale, scale);
        Ellipse(g, Color.FromArgb(175, Color.White), 0, 12, 90, 22);
        Ellipse(g, Color.FromArgb(175, Color.White), 18, 0, 40, 32);
        Ellipse(g, Color.FromArgb(175, Color.White), 48, 8, 30, 24);
        g.Restore(state);
    }

    private static void Leaf(Graphics g, float x, float y, float angle)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.RotateTransform(angle);
        using var stem = new Pen(Color.FromArgb(114, 154, 90), 2);
        g.DrawLine(stem, 0, 30, 0, -24);
        Ellipse(g, Color.FromArgb(139, 177, 108), -18, -22, 18, 32);
        Ellipse(g, Color.FromArgb(151, 188, 119), 1, -8, 18, 32);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _logo.Dispose();
        base.Dispose(disposing);
    }
}
