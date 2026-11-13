using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Spread;

/// <summary>The settings view of the GitHub menu: which organisation / team (PRO id) to watch, which severities count, toasts. Nothing is saved when an entry is invalid.</summary>
internal static class GitHubSettings
{
    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static FrameworkElement Build(BarContext ctx, Config cfg, Action close)
    {
        var g = cfg.GitHub;
        var root = new StackPanel { Width = 470 };
        root.Children.Add(BarUi.Txt("Dependabot settings", 16));
        root.Children.Add(new TextBlock { Text = "Which repositories to watch: those whose custom property matches (like the filter on github.com), or a fixed list. Spread only reads.", Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        TextBox Field(string label, string value, string? tip = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = BarUi.Txt(label, 12.5, BarUi.Dim, FontWeights.Normal); l.Margin = new Thickness(0, 6, 8, 6);
            var box = new TextBox { Text = value, Height = 26, Padding = new Thickness(7, 2, 7, 2), FontSize = 12.5, Background = Fz(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Fz(Color.FromArgb(0x40, 255, 255, 255)), Margin = new Thickness(0, 3, 0, 3), ToolTip = tip };
            box.PreviewMouseDown += (_, _) => { Popover.RequestFocus(); ctx.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); Keyboard.Focus(box); }), DispatcherPriority.Input); };
            Grid.SetRow(l, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            grid.Children.Add(l); grid.Children.Add(box);
            row++;
            return box;
        }
        CheckBox Check(string label, bool value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, FontSize = 12.5, Margin = new Thickness(0, 5, 0, 5) };
            Grid.SetRow(c, row); Grid.SetColumn(c, 1); grid.Children.Add(c);
            row++;
            return c;
        }

        var org = Field("Organisation", g.Org, "e.g. Contoso");
        var propName = Field("Team property", g.PropertyName, "The custom property on the repositories, e.g. ProductTeam");
        var propValue = Field("Team id (property value)", g.PropertyValue, "e.g. TEAM-1");
        var repos = Field("Or fixed repositories", string.Join(", ", g.Repos), "owner/name, comma separated. When filled, the team property is not used.");
        var bar = Field("Number in the bar counts", string.Join(", ", g.BarSeverities), "critical, high, medium, low");
        var list = Field("Menu lists", string.Join(", ", g.ListSeverities), "critical, high, medium, low - more severities = more requests");
        var refresh = Field("Refresh every (minutes)", g.RefreshMinutes.ToString());
        var sla = Field("Mark older than (days, 0 = off)", g.SlaDays.ToString());
        var toastOn = Check("Sticky toast for new critical alerts", g.Toast.Enabled);
        var toastHigh = Check("   also for new high alerts", g.Toast.OnHigh);
        var toastSec = Field("Toast stays (seconds, 0 = until closed)", g.Toast.Seconds.ToString());
        var ghPath = Field("gh.exe (only if not found)", g.GhPath, "Empty = found automatically");
        root.Children.Add(grid);

        var msg = new TextBlock { Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        root.Children.Add(msg);

        GitHubLogic.SettingsInput Input() => new(org.Text, propName.Text, propValue.Text, repos.Text, bar.Text, list.Text, refresh.Text, sla.Text, toastSec.Text, ghPath.Text);

        var buttons = new WrapPanel();
        buttons.Children.Add(BarUi.Button("Save", () =>
        {
            var errors = GitHubLogic.Validate(Input());
            if (errors.Count > 0) { msg.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); msg.Text = string.Join("\n", errors); return; }
            static string[] Split(string s) => s.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var newRepos = Split(repos.Text);
            bool teamChanged = !string.Equals(org.Text.Trim(), g.Org, StringComparison.OrdinalIgnoreCase) || propName.Text.Trim() != g.PropertyName || propValue.Text.Trim() != g.PropertyValue || !newRepos.SequenceEqual(g.Repos);
            g.Org = org.Text.Trim(); g.PropertyName = propName.Text.Trim(); g.PropertyValue = propValue.Text.Trim(); g.Repos = newRepos;
            g.BarSeverities = Split(bar.Text).Select(s => s.ToLowerInvariant()).Distinct().ToArray();
            g.ListSeverities = Split(list.Text).Select(s => s.ToLowerInvariant()).Distinct().ToArray();
            g.RefreshMinutes = int.Parse(refresh.Text.Trim()); g.SlaDays = int.Parse(sla.Text.Trim());
            g.Toast.Enabled = toastOn.IsChecked == true; g.Toast.OnHigh = toastHigh.IsChecked == true; g.Toast.Seconds = int.Parse(toastSec.Text.Trim());
            g.GhPath = ghPath.Text.Trim();
            cfg.Save();
            GitHubData.Reset(reseed: teamChanged);   // another team: its existing alerts are remembered silently, not toasted
            close();
        }));
        buttons.Children.Add(BarUi.Button("Cancel", close));
        buttons.Children.Add(BarUi.Button("Test", async () =>
        {
            var errors = GitHubLogic.Validate(Input());
            if (errors.Count > 0) { msg.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); msg.Text = string.Join("\n", errors); return; }
            msg.Foreground = BarUi.Dim; msg.Text = "Testing…";
            var test = new GitHubConfig { Host = g.Host, Org = org.Text.Trim(), PropertyName = propName.Text.Trim(), PropertyValue = propValue.Text.Trim(), GhPath = ghPath.Text.Trim(), Repos = repos.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            msg.Text = await GitHubData.TestAsync(test);
        }));
        buttons.Children.Add(BarUi.Button("Open config file", () => { Popover.CloseAny(); try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{Config.FilePath}\"") { UseShellExecute = true }); } catch { } }));
        root.Children.Add(buttons);
        return root;
    }
}
