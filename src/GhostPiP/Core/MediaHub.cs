using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace GhostPiP.Core;

/// <summary>What Windows says one app is playing. A snapshot: replaced, never mutated.</summary>
public sealed record MediaState(string Id, string Title, string Artist, bool IsPlaying, TimeSpan Position, TimeSpan Duration,
    DateTimeOffset Stamp, bool CanSeek, bool CanNext, bool CanPrevious)
{
    public TimeSpan PositionNow =>
        IsPlaying && Duration > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Min(Duration.Ticks, (Position + (DateTimeOffset.UtcNow - Stamp)).Ticks))
            : Position;

    public double Progress => Duration > TimeSpan.Zero ? Math.Clamp(PositionNow.TotalSeconds / Duration.TotalSeconds, 0, 1) : 0;
}

/// <summary>
/// The system media sessions (SMTC). This is what makes play/pause talk to the mirrored app rather than
/// whatever Windows thinks is current, and what gives the controller a real title and progress bar.
/// </summary>
public sealed class MediaHub
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly Dictionary<string, MediaState> _states = new();
    readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = new();
    readonly object _gate = new();
    int _refreshing;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await RefreshAsync();
        }
        catch (Exception)
        {
            // Older or stripped-down Windows: playback buttons fall back to media keys.
            _manager = null;
        }
    }

    /// <summary>Polled rather than event-driven: the session events are not reliable across browsers.</summary>
    public async Task RefreshAsync()
    {
        if (_manager is null || Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            var sessions = _manager.GetSessions();
            var states = new Dictionary<string, MediaState>();
            var live = new Dictionary<string, GlobalSystemMediaTransportControlsSession>();
            foreach (var session in sessions)
            {
                string id = session.SourceAppUserModelId ?? "";
                try
                {
                    var playback = session.GetPlaybackInfo();
                    var timeline = session.GetTimelineProperties();
                    var media = await session.TryGetMediaPropertiesAsync();
                    var controls = playback.Controls;
                    states[id] = new MediaState(
                        id,
                        media?.Title ?? "",
                        media?.Artist ?? "",
                        playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        timeline.Position,
                        timeline.EndTime - timeline.StartTime,
                        timeline.LastUpdatedTime,
                        controls.IsPlaybackPositionEnabled,
                        controls.IsNextEnabled,
                        controls.IsPreviousEnabled);
                    live[id] = session;
                }
                catch (Exception)
                {
                    // A session can vanish while we read it.
                }
            }
            lock (_gate)
            {
                _states.Clear();
                _sessions.Clear();
                foreach (var (id, state) in states)
                {
                    _states[id] = state;
                    _sessions[id] = live[id];
                }
            }
        }
        catch (Exception)
        {
            // Treat it as "nothing playing" until the next poll.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>The media session that belongs to a window, matched on title first and app identity second.</summary>
    public MediaState? Find(string windowTitle, string exeName)
    {
        string stem = Path.GetFileNameWithoutExtension(exeName);
        MediaState? best = null;
        int bestScore = 0;
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                int score = Score(state, windowTitle, stem);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = state;
                }
            }
        }
        return bestScore >= 2 ? best : null;
    }

    public static int Score(MediaState state, string windowTitle, string exeStem)
    {
        int score = 0;
        string id = state.Id;
        bool titleMatches = state.Title.Length > 2 && windowTitle.Contains(state.Title, StringComparison.OrdinalIgnoreCase);
        bool appMatches = exeStem.Length > 0 && id.Contains(exeStem, StringComparison.OrdinalIgnoreCase);
        // Firefox identifies itself by an install hash rather than its name.
        if (exeStem.Equals("firefox", StringComparison.OrdinalIgnoreCase) && id.Length == 16 && id.All(char.IsAsciiHexDigit))
            appMatches = true;
        if (exeStem.Equals("msedge", StringComparison.OrdinalIgnoreCase) && id.Contains("edge", StringComparison.OrdinalIgnoreCase))
            appMatches = true;

        if (titleMatches)
            score += 4;
        if (appMatches)
            score += 2;
        if (state.IsPlaying && score > 0)
            score += 1;
        return score;
    }

    public bool AnyPlayingFor(string windowTitle, string exeName) => Find(windowTitle, exeName)?.IsPlaying == true;

    GlobalSystemMediaTransportControlsSession? Session(string id)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }

    public async Task<bool> TogglePlayAsync(string id)
    {
        try
        {
            return Session(id) is { } s && await s.TryTogglePlayPauseAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> NextAsync(string id)
    {
        try
        {
            return Session(id) is { } s && await s.TrySkipNextAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> PreviousAsync(string id)
    {
        try
        {
            return Session(id) is { } s && await s.TrySkipPreviousAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> SeekAsync(MediaState state, TimeSpan to)
    {
        if (!state.CanSeek || state.Duration <= TimeSpan.Zero)
            return false;
        try
        {
            long ticks = Math.Clamp(to.Ticks, 0, state.Duration.Ticks);
            return Session(state.Id) is { } s && await s.TryChangePlaybackPositionAsync(ticks);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<BitmapSource?> ArtAsync(string id)
    {
        try
        {
            if (Session(id) is not { } s)
                return null;
            var media = await s.TryGetMediaPropertiesAsync();
            if (media?.Thumbnail is null)
                return null;
            using var stream = await media.Thumbnail.OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
