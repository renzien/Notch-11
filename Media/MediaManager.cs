using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace DynamicIsland.Media;

public class MediaManager : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly object _lock = new();

    public event Action<AppMediaState>? MediaStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                UpdateCurrentSession();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GSMTC Init error: {ex.Message}");
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession();
    }

    private void UpdateCurrentSession()
    {
        lock (_lock)
        {
            if (_sessionManager == null) return;

            var newSession = _sessionManager.GetCurrentSession();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }

        _ = RefreshMediaInfoAsync();
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        await RefreshMediaInfoAsync();
    }

    private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        await RefreshMediaInfoAsync();
    }

    private async void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        await RefreshMediaInfoAsync();
    }

    public async Task RefreshMediaInfoAsync()
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_lock)
        {
            session = _currentSession;
        }

        if (session == null)
        {
            MediaStateChanged?.Invoke(new AppMediaState { HasMedia = false });
            return;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var mediaProps = await session.TryGetMediaPropertiesAsync();
            if (mediaProps == null || string.IsNullOrWhiteSpace(mediaProps.Title))
            {
                MediaStateChanged?.Invoke(new AppMediaState { HasMedia = false, IsPlaying = false });
                return;
            }

            TimeSpan position = TimeSpan.Zero;
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    position = timeline.Position;
                    duration = timeline.EndTime - timeline.StartTime;
                    if (duration <= TimeSpan.Zero) duration = timeline.EndTime;

                    if (isPlaying && timeline.LastUpdatedTime != default)
                    {
                        var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                        if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromHours(3))
                        {
                            position += elapsed;
                        }
                    }

                    if (duration > TimeSpan.Zero && position > duration)
                    {
                        position = duration;
                    }
                    if (position < TimeSpan.Zero)
                    {
                        position = TimeSpan.Zero;
                    }
                }
            }
            catch { }

            BitmapImage? thumbnail = null;
            if (mediaProps.Thumbnail != null)
            {
                try
                {
                    using var ras = await mediaProps.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();

                    thumbnail = new BitmapImage();
                    thumbnail.BeginInit();
                    // Batasi resolusi agar hemat RAM (<30MB)
                    thumbnail.DecodePixelWidth = 120;
                    thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                    thumbnail.StreamSource = stream;
                    thumbnail.EndInit();
                    thumbnail.Freeze(); // Wajib untuk thread safety dan GC
                }
                catch
                {
                    thumbnail = null;
                }
            }

            var state = new AppMediaState
            {
                HasMedia = true,
                IsPlaying = isPlaying,
                Title = mediaProps.Title ?? "Unknown Title",
                Artist = mediaProps.Artist ?? "Unknown Artist",
                Thumbnail = thumbnail,
                SourceAppId = session.SourceAppUserModelId ?? string.Empty,
                Position = position,
                Duration = duration
            };

            MediaStateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching media props: {ex.Message}");
        }
    }

    public bool TryGetLiveTimeline(out TimeSpan position, out TimeSpan duration, out bool isPlaying)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        isPlaying = false;

        GlobalSystemMediaTransportControlsSession? session;
        lock (_lock)
        {
            session = _currentSession;
        }

        if (session == null) return false;

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var timeline = session.GetTimelineProperties();
            if (timeline == null) return false;

            position = timeline.Position;
            duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero) duration = timeline.EndTime;

            if (isPlaying && timeline.LastUpdatedTime != default)
            {
                var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromHours(3))
                {
                    position += elapsed;
                }
            }

            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task PlayPauseAsync()
    {
        if (_currentSession != null)
        {
            var info = _currentSession.GetPlaybackInfo();
            if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await _currentSession.TryPauseAsync();
            }
            else
            {
                await _currentSession.TryPlayAsync();
            }
        }
    }

    public async Task NextTrackAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipNextAsync();
        }
    }

    public async Task PreviousTrackAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipPreviousAsync();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _currentSession = null;
            }
        }
    }
}
