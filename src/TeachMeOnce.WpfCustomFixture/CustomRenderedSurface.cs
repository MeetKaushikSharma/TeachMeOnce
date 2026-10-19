using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TeachMeOnce.WpfCustomFixture;

public sealed class CustomRenderedSurface : FrameworkElement
{
    public static readonly Rect TargetDipRect = new(60, 90, 220, 110);
    public static readonly Rect DecoyDipRect = new(360, 90, 220, 110);

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 36, 48));
    private static readonly Brush TargetNormalBrush = new SolidColorBrush(Color.FromRgb(40, 180, 120));
    private static readonly Brush TargetPressedBrush = new SolidColorBrush(Color.FromRgb(230, 160, 40));
    private static readonly Brush DecoyBrush = new SolidColorBrush(Color.FromRgb(60, 120, 190));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(230, 235, 245));
    private bool _pressed;

    public CustomRenderedSurface()
    {
        Focusable = false;
        IsHitTestVisible = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawRegion(drawingContext, TargetDipRect, _pressed ? TargetPressedBrush : TargetNormalBrush, "CUSTOM TARGET");
        DrawRegion(drawingContext, DecoyDipRect, DecoyBrush, "DECOY REGION");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (TargetDipRect.Contains(point))
        {
            _pressed = !_pressed;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static void DrawRegion(DrawingContext drawingContext, Rect rectangle, Brush fill, string label)
    {
        drawingContext.DrawRectangle(fill, new Pen(BorderBrush, 2), rectangle);
        var text = new FormattedText(
            label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            18,
            Brushes.White,
            1.0);
        drawingContext.DrawText(text, new Point(rectangle.Left + 16, rectangle.Top + 42));
    }
}
