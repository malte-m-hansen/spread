using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Spread;

internal sealed record MediaInfo(string Title, string Artist, bool Playing);

/// <summary>"Now playing" through the Windows media session manager (the same source as the volume flyout). Call from a worker thread.</summary>
internal static class MediaProbe
{
    static GlobalSystemMediaTransportControlsSessionManager? _mgr;

    static GlobalSystemMediaTransportControlsSessionManager Mgr() =>
        _mgr ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();

    public static MediaInfo? Read()
    {
        try
        {
            var s = Mgr().GetCurrentSession();
            if (s == null) return null;
            var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
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
                    case "toggle": s.TryTogglePlayPauseAsync().AsTask().GetAwaiter().GetResult(); break;
                    case "next": s.TrySkipNextAsync().AsTask().GetAwaiter().GetResult(); break;
                    case "prev": s.TrySkipPreviousAsync().AsTask().GetAwaiter().GetResult(); break;
                }
            }
            catch (Exception e) { Log.Write("media command failed: " + e.Message); }
        });
    }
}
