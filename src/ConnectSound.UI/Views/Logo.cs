using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ConnectSound.UI.Views;

/// <summary>The mark: three channel meters on an ink key, the middle one carrying the signal.</summary>
public sealed class Logo : Control
{
    private static readonly IBrush Key = new SolidColorBrush(Color.Parse("#1A1F29"));
    private static readonly IBrush Side = new SolidColorBrush(Color.Parse("#C9CED6"));
    private static readonly IBrush Signal = new SolidColorBrush(Color.Parse("#F0A202"));

    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        context.DrawRectangle(Key, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

        var bar = s * 0.14;
        var gap = s * 0.08;
        var left = (s - 3 * bar - 2 * gap) / 2;
        var bottom = s * 0.76;
        double[] heights = [0.32, 0.54, 0.40];
        for (var i = 0; i < 3; i++)
        {
            var h = s * heights[i];
            var rect = new Rect(left + i * (bar + gap), bottom - h, bar, h);
            context.DrawRectangle(i == 1 ? Signal : Side, null, rect, bar / 2, bar / 2);
        }
    }
}
