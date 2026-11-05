using System;
using Avalonia;
using Avalonia.Controls;

namespace FireAxe.Views;

/// <summary>Independent vertical lanes keep collapsed folders compact beside expanded ones.</summary>
public sealed class ModTargetPanel : Panel
{
    private const double Gap = 8;
    private static int Columns(double width) => width >= 1100 ? 2 : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 700;
        var columns = Columns(width);
        var laneWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(laneWidth, double.PositiveInfinity));
            heights[i % columns] += Children[i].DesiredSize.Height + Gap;
        }
        return new Size(width, Math.Max(0, Math.Max(heights[0], heights[^1]) - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var laneWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        for (var i = 0; i < Children.Count; i++)
        {
            var lane = i % columns;
            var height = Children[i].DesiredSize.Height;
            Children[i].Arrange(new Rect(lane * (laneWidth + Gap), heights[lane], laneWidth, height));
            heights[lane] += height + Gap;
        }
        return finalSize;
    }
}
