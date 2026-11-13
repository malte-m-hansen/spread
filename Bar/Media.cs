using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Spread;

internal sealed record MediaInfo(string Title, string Artist, bool Playing);

/// <summary>"Now playing" through the Windows media session manager (the same source as the volume flyout). Call from a worker thread.</summary>
internal static class MediaProbe
{
    static GlobalSystemMediaTransportControlsSessionManager? _mgr;

    /// <summary>Waits at most 5 s: the answer comes from the playing app, and one that never answers would keep the widget's worker
    /// (and its busy flag) forever, so "now playing" would stop updating for the rest of the session.</summary>
    static T Within<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        var t = op.AsTask();
        if (!t.Wait(5000)) throw new TimeoutException();
        return t.Result;
    }

    static GlobalSystemMediaTransportControlsSessionManager Mgr() =>
        _mgr ??= Within(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());

    public static MediaInfo? Read()
    {
        try
        {
            var s = Mgr().GetCurrentSession();
            if (s == null) return null;
            var p = Within(s.TryGetMediaPropertiesAsync());
            if (string.IsNullOrWhiteSpace(p.Title)) return null;
            var st = s.GetPlaybackInfo().PlaybackStatus;
            if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed || st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped) return null;
            return new MediaInfo(p.Title, p.Artist ?? "", st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }
        catch { _mgr = null; return null; }
    }

    public static void Command(string cmd)
    {
        Task.Run(() =>
        {
            try
            {
                var s = Mgr().GetCurrentSession();
                if (s == null) return;
                switch (cmd)
                {
                    case "toggle": Within(s.TryTogglePlayPauseAsync()); break;
                    case "next": Within(s.TrySkipNextAsync()); break;
                    case "prev": Within(s.TrySkipPreviousAsync()); break;
                }
            }
            catch (Exception e) { Log.Write("media command failed: " + e.GetType().Name); }
        });
    }
}
