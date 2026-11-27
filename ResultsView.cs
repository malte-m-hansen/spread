using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Spread;

/// <summary>
/// The block under the search box: the line of icons (apps, settings, tools), the Everything file list and the key hint. The overlay and a
/// test build the same thing, so the test can check that the block does not rearrange itself when the file list arrives.
/// </summary>
internal sealed class ResultsView
{
    public readonly Border Host = new();
    public readonly StackPanel Panel = new() { Orientation = Orientation.Vertical };
    public readonly ScrollViewer Strip = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public readonly StackPanel StripPanel = new() { Orientation = Orientation.Horizontal };
    public readonly Border FilesBox = new();
    public readonly StackPanel FilesPanel = new();
    public readonly TextBlock KeyHint = new() { Foreground = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)), FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(8, 8, 0, 0), IsHitTestVisible = false };

    public ResultsView()
    {
        Strip.Content = StripPanel;
        // The block is as wide as its widest part: the icons alone at first, up to 980 px once the Everything list arrives. Centred, the icons and
        // the hint stay where they are when it does (left-aligned they slid to the left edge of the wider block).
        StripPanel.HorizontalAlignment = HorizontalAlignment.Center;
        KeyHint.HorizontalAlignment = HorizontalAlignment.Center;
        FilesBox.Child = FilesPanel;
        FilesBox.Visibility = Visibility.Collapsed;
        FilesBox.Margin = new Thickness(0, 14, 0, 0); FilesBox.Padding = new Thickness(10, 8, 10, 8); FilesBox.CornerRadius = new CornerRadius(16);
        FilesBox.Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        Panel.Children.Add(Strip); Panel.Children.Add(FilesBox); Panel.Children.Add(KeyHint);
        Host.Child = Panel;
        Host.VerticalAlignment = VerticalAlignment.Top; Host.HorizontalAlignment = HorizontalAlignment.Center;
        Host.Margin = new Thickness(0, 150, 0, 0);
        Host.Opacity = 0; Host.Visibility = Visibility.Collapsed;
    }
}
