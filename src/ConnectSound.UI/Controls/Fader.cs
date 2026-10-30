using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ConnectSound.UI.Controls;

/// <summary>
/// A console channel fader: a groove with a scale and a ridged cap. Drag or tap to move,
/// arrow keys to nudge, double-tap to return to unity.
/// </summary>
public sealed class Fader : RangeBase
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<Fader, Orientation>(nameof(Orientation), Orientation.Vertical);

    public static readonly StyledProperty<double> UnityProperty =
        AvaloniaProperty.Register<Fader, double>(nameof(Unity), 1.0);

    private static readonly IBrush Groove = new SolidColorBrush(Color.Parse("#1A1F29"), 0.85);
    private static readonly IBrush Tick = new SolidColorBrush(Color.Parse("#1A1F29"), 0.35);
    private static readonly IBrush CapBody = new SolidColorBrush(Color.Parse("#2A303C"));
    private static readonly IBrush CapRidge = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.14);
    private static readonly IBrush CapLine = new SolidColorBrush(Color.Parse("#F0A202"));
    private static readonly IBrush FocusRing = new SolidColorBrush(Color.Parse("#F0A202"), 0.55);

    private const double CapLength = 44;
    private const double CapThickness = 30;
    private bool _dragging;

    static Fader()
    {
        FocusableProperty.OverrideDefaultValue<Fader>(true);
        MaximumProperty.OverrideDefaultValue<Fader>(1.5);
        SmallChangeProperty.OverrideDefaultValue<Fader>(0.05);
        AffectsRender<Fader>(ValueProperty, OrientationProperty, IsFocusedProperty);
    }

    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public double Unity { get => GetValue(UnityProperty); set => SetValue(UnityProperty, value); }

    private bool Vertical => Orientation == Orientation.Vertical;
    private double Travel => Math.Max(1, (Vertical ? Bounds.Height : Bounds.Width) - CapLength);

    private double PositionOf(double value)
    {
        var t = (value - Minimum) / Math.Max(1e-9, Maximum - Minimum);
        return Vertical ? CapLength / 2 + (1 - t) * Travel : CapLength / 2 + t * Travel;
    }

    private double ValueAt(Point p)
    {
        var along = (Vertical ? p.Y : p.X) - CapLength / 2;
        var t = Math.Clamp(along / Travel, 0, 1);
        if (Vertical) t = 1 - t;
        var value = Minimum + t * (Maximum - Minimum);
        // A gentle detent at unity, like the notch on a real fader.
        return Math.Abs(value - Unity) < (Maximum - Minimum) * 0.025 ? Unity : value;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var center = Vertical ? w / 2 : h / 2;

        // Makes the whole area hit-testable, not just the painted parts.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // Groove.
        var groove = Vertical
            ? new Rect(center - 2, CapLength / 2, 4, Travel)
            : new Rect(CapLength / 2, center - 2, Travel, 4);
        context.DrawRectangle(Groove, null, groove, 2, 2);

        // Scale: every 25 %, with a longer mark at unity.
        for (var v = Minimum; v <= Maximum + 1e-9; v += (Maximum - Minimum) / 6)
        {
            var pos = PositionOf(v);
            var isUnity = Math.Abs(v - Unity) < 1e-6;
            var len = isUnity ? 12 : 7;
            var rect = Vertical
                ? new Rect(center + CapThickness / 2 + 3, pos - 0.5, len, isUnity ? 2 : 1)
                : new Rect(pos - 0.5, center + CapThickness / 2 + 3, isUnity ? 2 : 1, len);
            context.DrawRectangle(Tick, null, rect);
        }

        // Cap.
        var capCenter = PositionOf(Value);
        var cap = Vertical
            ? new Rect(center - CapThickness / 2, capCenter - CapLength / 2, CapThickness, CapLength)
            : new Rect(capCenter - CapLength / 2, center - CapThickness / 2, CapLength, CapThickness);

        if (IsFocused) context.DrawRectangle(null, new Pen(FocusRing, 3), cap.Inflate(3), 6, 6);
        context.DrawRectangle(CapBody, null, cap, 4, 4);

        for (var i = -3; i <= 3; i++)
        {
            if (i == 0) continue;
            var o = capCenter + i * 5;
            var ridge = Vertical
                ? new Rect(cap.X + 5, o - 0.5, cap.Width - 10, 1)
                : new Rect(o - 0.5, cap.Y + 5, 1, cap.Height - 10);
            context.DrawRectangle(CapRidge, null, ridge);
        }

        var line = Vertical
            ? new Rect(cap.X + 3, capCenter - 1, cap.Width - 6, 2)
            : new Rect(capCenter - 1, cap.Y + 3, 2, cap.Height - 6);
        context.DrawRectangle(CapLine, null, line);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Vertical ? new Size(CapThickness + 20, 180) : new Size(180, CapThickness + 20);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            Value = Unity;
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        Value = ValueAt(e.GetPosition(this));
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        Value = ValueAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var delta = e.Key switch
        {
            Key.Up or Key.Right => SmallChange,
            Key.Down or Key.Left => -SmallChange,
            Key.PageUp => LargeChange,
            Key.PageDown => -LargeChange,
            _ => 0,
        };
        if (e.Key == Key.Home) { Value = Unity; e.Handled = true; }
        if (delta == 0) return;
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
        e.Handled = true;
    }
}
