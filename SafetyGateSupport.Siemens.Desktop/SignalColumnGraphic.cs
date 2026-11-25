using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SafetyGateSupport.Siemens;

namespace SiemensUsbCheck;

/// <summary>
/// Draws a signal column the way it stands on the gate: a stack of lens segments on a base,
/// segment 1 next to the base (as the Siemens configurator's simulator draws it).
/// </summary>
internal static class SignalColumnGraphic
{
    private static readonly Color HousingTop = Color.FromRgb(70, 78, 87), HousingBottom = Color.FromRgb(31, 36, 41), Off = Color.FromRgb(214, 220, 226);

    /// <param name="segmentHeight">Height of one lens segment.</param>
    /// <param name="labels">Show segment numbers on the left and effects on the right.</param>
    public static FrameworkElement Build(SignalStatePreview.Appearance look, double segmentHeight, double width, bool upsideDown, bool labels)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true };
        if (labels)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        int column = labels ? 1 : 0;

        // Top to bottom: cap, segments 9..1, base. Upside-down mounting reverses the stack.
        var parts = new List<(FrameworkElement Body, SignalStatePreview.Segment? Segment)>
        {
            (Cap(look, width, segmentHeight, upsideDown), null)
        };
        foreach (var segment in look.Segments.Reverse()) parts.Add((Lens(segment, width, segmentHeight), segment));
        parts.Add((Base(width, segmentHeight, upsideDown), null));
        if (upsideDown) parts.Reverse();

        for (int row = 0; row < parts.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (body, segment) = parts[row];
            Grid.SetRow(body, row); Grid.SetColumn(body, column); grid.Children.Add(body);
            if (!labels || segment is null) continue;
            var number = new TextBlock { Text = segment.Number.ToString(), FontSize = 11, Opacity = 0.65, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(number, row); grid.Children.Add(number);
            var effect = new TextBlock { Text = segment.IsOff ? "" : segment.EffectName, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(effect, row); Grid.SetColumn(effect, 2); grid.Children.Add(effect);
        }
        return grid;
    }

    public static Brush LensBrush(SignalStatePreview.Segment segment)
    {
        Color center;
        double opacity = 1;
        if (segment.IsOff) center = Off;
        else { var (r, g, b) = segment.DisplayColor; center = Color.FromRgb(r, g, b); opacity = segment.DisplayOpacity; }
        // A cylinder: darker at both edges, a soft highlight left of centre.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), Opacity = opacity };
        brush.GradientStops.Add(new GradientStop(Shade(center, 0.72), 0));
        brush.GradientStops.Add(new GradientStop(Blend(center, Colors.White, segment.IsOff ? 0.35 : 0.28), 0.3));
        brush.GradientStops.Add(new GradientStop(center, 0.55));
        brush.GradientStops.Add(new GradientStop(Shade(center, 0.78), 1));
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement Lens(SignalStatePreview.Segment segment, double width, double height) => new Border
    {
        Width = width, Height = height, Background = LensBrush(segment),
        BorderBrush = new SolidColorBrush(Color.FromArgb(110, 30, 36, 42)), BorderThickness = new Thickness(1, 0, 1, Math.Max(1, height / 14)),
        ToolTip = $"Segment {segment.Number}: {(segment.IsOff ? "off" : segment.EffectName)}"
    };

    private static FrameworkElement Cap(SignalStatePreview.Appearance look, double width, double segmentHeight, bool upsideDown)
    {
        bool sound = look.SoundMode != 0 && look.SoundVolume != 0;
        var cap = new Border
        {
            Width = width, Height = Math.Max(4, segmentHeight * (sound ? 1.1 : 0.55)),
            CornerRadius = upsideDown ? new CornerRadius(0, 0, width / 3, width / 3) : new CornerRadius(width / 3, width / 3, 0, 0),
            Background = HousingBrush(),
            ToolTip = sound ? $"Sound on (mode {look.SoundMode}, volume {look.SoundVolume})" : "Sound off"
        };
        if (sound)
        {
            // Speaker slots on the sounder module.
            var slots = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 3; i++) slots.Children.Add(new Border { Width = width * 0.55, Height = Math.Max(1, segmentHeight / 12), Margin = new Thickness(0, Math.Max(1, segmentHeight / 16), 0, 0), Background = new SolidColorBrush(Color.FromRgb(140, 150, 160)), CornerRadius = new CornerRadius(1) });
            cap.Child = slots;
        }
        return cap;
    }

    private static FrameworkElement Base(double width, double segmentHeight, bool upsideDown) => new Border
    {
        Width = width * 1.12, Height = Math.Max(5, segmentHeight * 1.2), HorizontalAlignment = HorizontalAlignment.Center,
        CornerRadius = upsideDown ? new CornerRadius(width / 8, width / 8, 2, 2) : new CornerRadius(2, 2, width / 8, width / 8),
        Background = HousingBrush()
    };

    private static Brush HousingBrush()
    {
        var brush = new LinearGradientBrush(HousingTop, HousingBottom, 0); brush.Freeze(); return brush;
    }

    private static Color Shade(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    private static Color Blend(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
