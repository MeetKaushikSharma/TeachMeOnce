using System.Drawing;
using System.Drawing.Drawing2D;

namespace TeachMeOnce.WinFormsCustomFixture;

public sealed class CustomRenderedSurface : Control
{
    public static readonly Rectangle TargetPixelRect = new(60, 90, 220, 110);
    public static readonly Rectangle DecoyPixelRect = new(360, 90, 220, 110);

    private static readonly Color TargetNormalColor = Color.FromArgb(40, 180, 120);
    private static readonly Color TargetPressedColor = Color.FromArgb(230, 160, 40);
    private static readonly Color DecoyColor = Color.FromArgb(60, 120, 190);
    private bool _pressed;

    public CustomRenderedSurface()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.FromArgb(30, 36, 48));
        DrawRegion(e.Graphics, TargetPixelRect, _pressed ? TargetPressedColor : TargetNormalColor, "CUSTOM TARGET");
        DrawRegion(e.Graphics, DecoyPixelRect, DecoyColor, "DECOY REGION");
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && TargetPixelRect.Contains(e.Location))
        {
            _pressed = !_pressed;
            Invalidate(TargetPixelRect);
        }
    }

    private static void DrawRegion(Graphics graphics, Rectangle rectangle, Color fill, string label)
    {
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Color.FromArgb(230, 235, 245), 2);
        using var font = new Font("Segoe UI", 18, FontStyle.Regular);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillRectangle(brush, rectangle);
        graphics.DrawRectangle(pen, rectangle);
        graphics.DrawString(label, font, Brushes.White, rectangle.Left + 16, rectangle.Top + 42);
    }
}
