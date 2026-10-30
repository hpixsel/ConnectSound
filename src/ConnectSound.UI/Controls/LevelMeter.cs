using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ConnectSound.UI.Controls;

/// <summary>Segmented LED meter: green body, amber from -10 dB, red in the last two segments, with a short peak hold.</summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<LevelMeter, Orientation>(nameof(Orientation), Orientation.Vertical);

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<LevelMeter, int>(nameof(Segments), 24);

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2E9E6B"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F0A202"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D93A2B"));
    private static readonly IBrush Off = new SolidColorBrush(Color.Parse("#1A1F29"), 0.09);

    private double _peak;
    private DateTime _peakAt;

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty, OrientationProperty, SegmentsProperty);
    }

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public int Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var count = Math.Max(4, Segments);
        var level = Math.Clamp(Level, 0, 1);
        var now = DateTime.UtcNow;
        if (level >= _peak || now - _peakAt > TimeSpan.FromMilliseconds(900))
        {
            _peak = level;
            _peakAt = now;
        }

        var lit = (int)Math.Round(level * count);
        var peakSegment = (int)Math.Round(_peak * count) - 1;
        var vertical = Orientation == Orientation.Vertical;
        var length = vertical ? Bounds.Height : Bounds.Width;
        var thickness = vertical ? Bounds.Width : Bounds.Height;
        const double gap = 2;
        var size = (length - gap * (count - 1)) / count;
        if (size <= 0) return;

        for (var i = 0; i < count; i++)
        {
            var on = i < lit || i == peakSegment && peakSegment > 0;
            var brush = on ? ColorFor(i, count) : Off;
            var offset = i * (size + gap);
            var rect = vertical
                ? new Rect(0, length - offset - size, thickness, size)
                : new Rect(offset, 0, size, thickness);
            context.DrawRectangle(brush, null, rect, 1, 1);
        }
    }

    private static IBrush ColorFor(int segment, int count) =>
        segment >= count - 2 ? Red : segment >= count * 0.78 ? Amber : Green;
}
