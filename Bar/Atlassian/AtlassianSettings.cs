using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Spread;

/// <summary>The settings view of the Atlassian panel (instead of editing config.json). Nothing is saved when an entry is invalid.</summary>
internal static class AtlassianSettings
{
    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static FrameworkElement Build(BarContext ctx, Config cfg, Action close)
    {
        var a = cfg.Atlassian;
        var root = new StackPanel { Width = 560 };
        root.Children.Add(BarUi.Txt("Jira and Confluence settings", 16));
        root.Children.Add(new TextBlock { Text = "Spread only reads from Atlassian. The token itself is kept in Windows Credential Manager, not here.", Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
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

        var board = Field("Board id", a.BoardId.ToString(), "The number in .../boards/12345");
        var project = Field("Project key", a.Project);
        var others = Field("Other projects (comma separated)", string.Join(", ", a.OtherProjects), "Shown in My work, e.g. OTHER");
        var space = Field("Confluence space", a.ConfluenceSpace);
        var stale = Field("Days without update = stale", a.StaleDays.ToString());
        var refresh = Field("Refresh sprint/OKR every (minutes)", a.RefreshMinutes.ToString());
        var umin = Field("Undone target - min (if the goal has none)", (a.UndoneTarget.Length > 0 ? a.UndoneTarget[0] : 3).ToString());
        var umax = Field("Undone target - max", (a.UndoneTarget.Length > 1 ? a.UndoneTarget[1] : 5).ToString());
        var expires = Field("Token expires (yyyy-mm-dd, optional)", a.TokenExpires, "The API cannot tell us; you get a warning 7 days before.");
        // new token: checked against Atlassian first, then stored in Windows Credential Manager (never in config.json); empty = keep the current one
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tl = BarUi.Txt("New token (leave empty to keep)", 12.5, BarUi.Dim, FontWeights.Normal); tl.Margin = new Thickness(0, 6, 8, 6);
        var tokenBox = new PasswordBox { Height = 26, Padding = new Thickness(7, 2, 7, 2), FontSize = 12.5, Background = Fz(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Fz(Color.FromArgb(0x40, 255, 255, 255)), Margin = new Thickness(0, 3, 0, 3), ToolTip = "Paste the new API token here and press Save. It is tested first; if Atlassian rejects it nothing is changed." };
        tokenBox.PreviewMouseDown += (_, _) => { Popover.RequestFocus(); ctx.Dispatcher.BeginInvoke(new Action(() => { tokenBox.Focus(); Keyboard.Focus(tokenBox); }), DispatcherPriority.Input); };
        Grid.SetRow(tl, row); Grid.SetRow(tokenBox, row); Grid.SetColumn(tokenBox, 1);
        grid.Children.Add(tl); grid.Children.Add(tokenBox);
        row++;
        var alertsOn = Check("Show a toast for mentions and assignments", a.Alerts.Enabled);
        var mentions = Check("   Jira: mentioned in a comment", a.Alerts.Mentions);
        var assigned = Check("   Jira: assigned to me", a.Alerts.Assigned);
        var confl = Check("   Confluence: mentioned", a.Alerts.ConfluenceMentions);
        var dnd = Check("No toasts while Windows notifications are off", a.Alerts.RespectDnd);
        var toastSec = Field("Toast stays (seconds)", a.Alerts.ToastSeconds.ToString());
        var poll = Field("Look for new mentions every (seconds)", a.Alerts.PollSeconds.ToString());
        var pos = Field("Toast position", a.Alerts.Position, "top-center, top-right or top-left");
        root.Children.Add(grid);

        var msg = new TextBlock { Foreground = BarUi.Dim, FontFamily = BarUi.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        root.Children.Add(msg);

        AtlassianLogic.SettingsInput Input() => new(board.Text, project.Text, others.Text, space.Text, stale.Text, refresh.Text, umin.Text, umax.Text, toastSec.Text, poll.Text, expires.Text, pos.Text);

        var buttons = new WrapPanel();
        buttons.Children.Add(BarUi.Button("Save", async () =>
        {
            var errors = AtlassianLogic.ValidateSettings(Input());
            if (errors.Count > 0) { msg.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); msg.Text = string.Join("\n", errors); return; }
            if (tokenBox.Password.Trim().Length > 0)
            {
                if (string.IsNullOrWhiteSpace(a.Site)) { msg.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); msg.Text = "Connect first (site and e-mail are not known yet)."; return; }
                msg.Foreground = BarUi.Dim; msg.Text = "Checking the new token…";
                string email = await System.Threading.Tasks.Task.Run(() => AtlassianData.EmailFor(cfg));   // may run whoami: not on the UI thread
                string? err = await AtlassianData.ConnectAsync(cfg, a.Site, email, tokenBox.Password);
                if (err != null) { msg.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); msg.Text = "Token not saved: " + err; return; }
                tokenBox.Clear();
                Log.Write("atlassian: token replaced");
            }
            string newProject = project.Text.Trim().ToUpperInvariant(), newSpace = space.Text.Trim();
            var newOthers = others.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToArray();
            bool reset = int.Parse(board.Text.Trim()) != a.BoardId || newProject != a.Project || newSpace != a.ConfluenceSpace || !newOthers.SequenceEqual(a.OtherProjects);
            a.BoardId = int.Parse(board.Text.Trim()); a.Project = newProject; a.OtherProjects = newOthers; a.ConfluenceSpace = newSpace;
            a.StaleDays = int.Parse(stale.Text.Trim()); a.RefreshMinutes = int.Parse(refresh.Text.Trim());
            a.UndoneTarget = new[] { int.Parse(umin.Text.Trim()), int.Parse(umax.Text.Trim()) };
            a.TokenExpires = expires.Text.Trim();
            a.Alerts.Enabled = alertsOn.IsChecked == true; a.Alerts.Mentions = mentions.IsChecked == true; a.Alerts.Assigned = assigned.IsChecked == true;
            a.Alerts.ConfluenceMentions = confl.IsChecked == true; a.Alerts.RespectDnd = dnd.IsChecked == true;
            a.Alerts.ToastSeconds = int.Parse(toastSec.Text.Trim()); a.Alerts.PollSeconds = int.Parse(poll.Text.Trim()); a.Alerts.Position = pos.Text.Trim().ToLowerInvariant();
            cfg.Save();
            if (reset) AtlassianData.ResetCaches(); else AtlassianData.RefreshNow();
            close();
        }));
        buttons.Children.Add(BarUi.Button("Cancel", close));
        buttons.Children.Add(BarUi.Button("Test connection", async () =>
        {
            msg.Foreground = BarUi.Dim; msg.Text = "Testing…";
            int.TryParse(board.Text.Trim(), out int b);
            msg.Text = await AtlassianData.TestAsync(cfg, b);
        }));
        buttons.Children.Add(BarUi.Button("Send test alert", () =>
        {
            // the typed duration only for this toast: the config is shared, so setting it for good outlived Cancel (and the next Save of any other setting wrote it)
            int saved = a.Alerts.ToastSeconds;
            a.Alerts.ToastSeconds = int.TryParse(toastSec.Text.Trim(), out int s) && s >= 3 && s <= 60 ? s : 10;
            try { AtlassianData.SendTestAlert(); } finally { a.Alerts.ToastSeconds = saved; }
        }));
        buttons.Children.Add(BarUi.Button("Disconnect", () => { AtlassianData.Disconnect(); close(); }));
        root.Children.Add(buttons);
        return root;
    }
}
