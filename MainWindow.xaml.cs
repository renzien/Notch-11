using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicIsland.Media;
using DynamicIsland.Native;

namespace DynamicIsland;

public enum IslandVisualState
{
    Compact,
    Expanded,
    ChargingPopup,
    TwsPopup,
    NotificationPopup
}

public partial class MainWindow : Window
{
    private IntPtr _hWnd;
    private readonly MediaManager _mediaManager;
    private readonly PowerMonitor _powerMonitor;
    private readonly AudioDeviceMonitor _audioMonitor;
    private readonly NotificationMonitor _notifMonitor;
    private readonly LyricsService _lyricsService;

    private AppMediaState? _currentMedia;
    private PowerInfo _currentPower;
    private IslandVisualState _currentState = IslandVisualState.Compact;
    private bool _isHovered;
    private bool _isLyricsExpanded;
    private int _activeExpandedPage = 0; // 0 = Media Player & Lyrics, 1 = Battery Status
    private CancellationTokenSource? _notificationCts;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private Storyboard? _spectrumStoryboard;
    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;
    private SettingsWindow? _settingsWindow;

    // Timeline Scrubber
    private TimeSpan _currentPosition = TimeSpan.Zero;
    private TimeSpan _currentDuration = TimeSpan.Zero;

    // Lirik (Timestamp Synced + Plain Fallback)
    private List<LyricItem> _syncedLyrics = new();
    private List<string> _plainLyrics = new();
    private int _currentLyricIndex = -1;
    private string _lastTrackKey = string.Empty;

    // Deteksi Aplikasi AI & Quick Prompt (Halaman 3)
    private List<DetectedAiApp> _openAiApps = new();
    private int _selectedAiAppIndex = 0;
    private bool _allowWindowActivation = false;
    private bool _isSendingPrompt = false;

    // SVG Geometries untuk Play / Pause
    private readonly Geometry _playGeometry = Geometry.Parse("M 5,3 L 16,10 L 5,17 Z");
    private readonly Geometry _pauseGeometry = Geometry.Parse("M 4,3 H 7.5 V 17 H 4 Z M 11.5,3 H 15 V 17 H 11.5 Z");

    // SVG Geometries untuk Notifikasi
    private readonly Geometry _chatGeometry = Geometry.Parse("M 4,4 L 16,4 A 2,2 0 0,1 18,6 L 18,14 A 2,2 0 0,1 16,16 L 8,16 L 4,20 L 4,6 A 2,2 0 0,1 4,4 Z");
    private readonly Geometry _shieldGeometry = Geometry.Parse("M 10,2 L 18,5 V 10 C 18,15 14,19 10,21 C 6,19 2,15 2,10 V 5 Z M 8.5,14 L 14.5,8 L 13,6.5 L 8.5,11 L 6.5,9 L 5,10.5 Z");
    private readonly Geometry _bellGeometry = Geometry.Parse("M 10,2 A 6,6 0 0,0 4,8 V 14 L 2,16 V 17 H 18 V 16 L 16,14 V 8 A 6,6 0 0,0 10,2 Z M 8,18 A 2,2 0 0,0 12,18 Z");
    private readonly Geometry _discordGeometry = Geometry.Parse("M 5,3 C 3.5,3 2,4.5 2,6 L 2,16 C 2,17.5 3.5,19 5,19 L 16,19 L 19,22 L 19,6 C 19,4.5 17.5,3 16,3 Z M 7.5,9.5 A 1.8,1.8 0 1,1 7.5,13.1 A 1.8,1.8 0 0,1 7.5,9.5 Z M 13.5,9.5 A 1.8,1.8 0 1,1 13.5,13.1 A 1.8,1.8 0 0,1 13.5,9.5 Z");

    // SVG Geometries untuk Baterai di Halaman 2
    private readonly Geometry _boltGeometry = Geometry.Parse("M 11,2 L 4,11 L 9,11 L 7,18 L 15,9 L 10,9 Z");
    private readonly Geometry _batteryOutlineGeometry = Geometry.Parse("M 4,6 H 15 A 2,2 0 0,1 17,8 V 14 A 2,2 0 0,1 15,16 H 4 A 2,2 0 0,1 2,14 V 8 A 2,2 0 0,1 4,6 Z M 18,10 H 19 V 12 H 18 Z");

    public MainWindow()
    {
        InitializeComponent();

        _mediaManager = new MediaManager();
        _powerMonitor = new PowerMonitor();
        _audioMonitor = new AudioDeviceMonitor();
        _notifMonitor = new NotificationMonitor();
        _lyricsService = new LyricsService();

        _currentPower = PowerMonitor.GetCurrentPowerInfo();

        _mediaManager.MediaStateChanged += OnMediaStateChanged;
        _powerMonitor.ChargerPluggedIn += OnChargerPluggedIn;
        _powerMonitor.PowerStateChanged += OnPowerStateChanged;
        _audioMonitor.TwsDeviceConnected += OnTwsDeviceConnected;
        _notifMonitor.NotificationReceived += OnNotificationReceived;
        _lyricsService.LyricsLoaded += OnLyricsLoaded;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppSettings.LoadAndApply();
        CenterWindowAtTop();
        InitializeTrayIcon();
        InitializeClock();
        InitializeLyricsTimer();
        InitializePlaybackTimer();
        UpdatePowerStateUI(_currentPower);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _notifMonitor.Dispose();
        await _mediaManager.InitializeAsync();
        await _notifMonitor.InitializeAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        _hWnd = helper.Handle;

        var exStyle = Win32.GetWindowLong(_hWnd, Win32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST;
        Win32.SetWindowLong(_hWnd, Win32.GWL_EXSTYLE, new IntPtr(newExStyle));

        var source = HwndSource.FromHwnd(_hWnd);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            if (_allowWindowActivation || _activeExpandedPage == 2)
            {
                return new IntPtr(Win32.MA_ACTIVATE);
            }
            handled = true;
            return new IntPtr(Win32.MA_NOACTIVATE);
        }

        if (msg == Win32.WM_POWERBROADCAST)
        {
            _powerMonitor.HandlePowerBroadcast();
        }

        return IntPtr.Zero;
    }

    private void SetWindowActivatable(bool activatable)
    {
        _allowWindowActivation = activatable;
        if (_hWnd == IntPtr.Zero) return;

        long exStyle = Win32.GetWindowLong(_hWnd, Win32.GWL_EXSTYLE).ToInt64();
        if (activatable)
        {
            exStyle &= ~Win32.WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle |= Win32.WS_EX_NOACTIVATE;
        }
        Win32.SetWindowLong(_hWnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void CenterWindowAtTop()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2.0;
        Top = 0; // Menempel sempurna pada bezel atas monitor
    }

    #region Digital Clock Handler

    private void InitializeClock()
    {
        CompactClockText.Text = DateTime.Now.ToString("HH:mm");

        _clockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _clockTimer.Tick += (s, e) =>
        {
            CompactClockText.Text = DateTime.Now.ToString("HH:mm");
            // Sinkronisasi persentase baterai real-time dengan Windows System
            var latestPower = _powerMonitor.Refresh();
            if (latestPower.BatteryPercentage != _currentPower.BatteryPercentage ||
                latestPower.IsPluggedIn != _currentPower.IsPluggedIn ||
                latestPower.HasBattery != _currentPower.HasBattery)
            {
                _currentPower = latestPower;
                UpdatePowerStateUI(latestPower);
            }

            if (_isHovered && _currentState == IslandVisualState.Expanded && _activeExpandedPage == 2 && !_isSendingPrompt)
            {
                RefreshOpenAiApps(false);
            }
        };
        _clockTimer.Start();
    }

    #endregion

    #region Media & Power & TWS & Notification Event Handlers

    private void OnMediaStateChanged(AppMediaState state)
    {
        Dispatcher.Invoke(() =>
        {
            _currentMedia = state;
            _currentPosition = state.Position;
            _currentDuration = state.Duration;

            UpdateMediaUI(state);
            UpdateTimelineUI();
            UpdateAlbumGradient(state.Thumbnail);

            // Cek perubahan lagu untuk mengambil lirik otomatis yang sinkron dengan durasi lagu
            string trackKey = $"{state.Artist} - {state.Title}".ToLowerInvariant();
            if (state.HasMedia && trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _syncedLyrics.Clear();
                _plainLyrics.Clear();
                _currentLyricIndex = -1;
                LyricLineAbove.Text = string.Empty;
                LyricLineCurrent.Text = "♪ Mengambil lirik lagu...";
                LyricLineBelow.Text = string.Empty;
                LyricLineBelow2.Text = string.Empty;

                _ = _lyricsService.FetchLyricsAsync(state.Title, state.Artist, state.Duration.TotalSeconds);
            }
            else if (state.HasMedia)
            {
                SyncLyricsWithTimeline();
            }

            if (_currentState is IslandVisualState.ChargingPopup or IslandVisualState.TwsPopup or IslandVisualState.NotificationPopup) return;

            if (_isHovered)
            {
                MorphTo(IslandVisualState.Expanded);
            }
            else
            {
                MorphTo(IslandVisualState.Compact);
            }
        });
    }

    private void UpdateMediaUI(AppMediaState state)
    {
        if (!state.HasMedia)
        {
            FullAlbumArt.Source = null;
            FullTitleText.Text = "No Media Playing";
            FullArtistText.Text = "Putar lagu di Spotify";
            PlayPauseIcon.Data = _playGeometry;
            LyricLineAbove.Text = string.Empty;
            LyricLineCurrent.Text = "♪ Menunggu pemutaran musik...";
            LyricLineBelow.Text = string.Empty;
            LyricLineBelow2.Text = string.Empty;

            StopSpectrumAnimation();
            UpdateCompactIcons();
            UpdateAlbumGradient(null);
            return;
        }

        FullAlbumArt.Source = state.Thumbnail;
        FullTitleText.Text = state.Title;
        FullArtistText.Text = state.Artist;

        string app = state.SourceAppId;
        if (app.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) app = "Spotify";
        else if (app.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) app = "Google Chrome";
        else if (app.Contains("Edge", StringComparison.OrdinalIgnoreCase)) app = "Microsoft Edge";
        else if (string.IsNullOrEmpty(app)) app = "Now Playing";
        SourceAppText.Text = app;

        PlayPauseIcon.Data = state.IsPlaying ? _pauseGeometry : _playGeometry;

        if (state.IsPlaying)
        {
            StartSpectrumAnimation();
        }
        else
        {
            StopSpectrumAnimation();
        }

        UpdateCompactIcons();
    }

    private void UpdateCompactIcons()
    {
        bool hasMusic = _currentMedia?.HasMedia ?? false;
        bool isPlaying = _currentMedia?.IsPlaying ?? false;
        bool isCharging = _currentPower.IsPluggedIn;

        if (hasMusic && isPlaying)
        {
            // Saat memutar musik:
            // 1. Cover album diganti jadi Logo Musik (♪)
            // 2. Jam diganti jadi Tulisan Judul Lagu
            CompactTitleText.Text = _currentMedia?.Title ?? "Music";
            CompactTitleText.Visibility = Visibility.Visible;
            CompactClockText.Visibility = Visibility.Collapsed;

            if (isCharging)
            {
                CompactChargerIcon.Visibility = Visibility.Visible;
                CompactMusicIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                CompactChargerIcon.Visibility = Visibility.Collapsed;
                CompactMusicIcon.Visibility = Visibility.Visible;
            }

            CompactSpectrumPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Saat tidak memutar musik:
            // Tampilkan Jam Digital (HH:mm) dan icon charger jika dicolok
            CompactTitleText.Visibility = Visibility.Collapsed;
            CompactClockText.Visibility = Visibility.Visible;
            CompactMusicIcon.Visibility = Visibility.Collapsed;
            CompactChargerIcon.Visibility = isCharging ? Visibility.Visible : Visibility.Collapsed;
            CompactSpectrumPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnChargerPluggedIn(PowerInfo power)
    {
        Dispatcher.Invoke(() =>
        {
            _currentPower = power;
            ShowChargingNotification(power);
        });
    }

    private void OnPowerStateChanged(PowerInfo power)
    {
        Dispatcher.Invoke(() =>
        {
            _currentPower = power;
            UpdatePowerStateUI(power);
        });
    }

    private void UpdatePowerStateUI(PowerInfo power)
    {
        ChargingPercentText.Text = $"{power.BatteryPercentage}%";
        UpdateCompactIcons();
        UpdateBatteryPageUI(power);

        if (!_isHovered && _currentState == IslandVisualState.Compact)
        {
            MorphTo(IslandVisualState.Compact);
        }
    }

    private async void ShowChargingNotification(PowerInfo power)
    {
        ChargingPercentText.Text = $"{power.BatteryPercentage}%";

        if (_currentState == IslandVisualState.ChargingPopup) return;

        _notificationCts?.Cancel();
        _notificationCts = new CancellationTokenSource();
        var token = _notificationCts.Token;

        MorphTo(IslandVisualState.ChargingPopup);

        try
        {
            await Task.Delay(3500, token);

            if (!token.IsCancellationRequested)
            {
                if (_isHovered || _isLyricsExpanded)
                {
                    MorphTo(IslandVisualState.Expanded);
                }
                else
                {
                    MorphTo(IslandVisualState.Compact);
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    private void OnTwsDeviceConnected(TwsDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            ShowTwsNotification(info);
        });
    }

    private async void ShowTwsNotification(TwsDeviceInfo info)
    {
        if (_currentState == IslandVisualState.TwsPopup) return;

        _notificationCts?.Cancel();
        _notificationCts = new CancellationTokenSource();
        var token = _notificationCts.Token;

        TwsDeviceNameText.Text = info.Name;
        TwsStatusText.Text = $"Connected · {info.BatteryPercentage}%";
        MorphTo(IslandVisualState.TwsPopup);

        try
        {
            await Task.Delay(3500, token);

            if (!token.IsCancellationRequested)
            {
                if (_isHovered || _isLyricsExpanded)
                {
                    MorphTo(IslandVisualState.Expanded);
                }
                else
                {
                    MorphTo(IslandVisualState.Compact);
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    private void OnNotificationReceived(AppNotificationInfo notif)
    {
        Dispatcher.Invoke(() =>
        {
            ShowAppNotification(notif);
        });
    }

    private async void ShowAppNotification(AppNotificationInfo notif)
    {
        _notificationCts?.Cancel();
        _notificationCts = new CancellationTokenSource();
        var token = _notificationCts.Token;

        string app = notif.AppName.ToLowerInvariant();
        if (app.Contains("whatsapp"))
        {
            NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 211, 102));
            NotifBadgeIcon.Data = _chatGeometry;
        }
        else if (app.Contains("line"))
        {
            NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 199, 85));
            NotifBadgeIcon.Data = _chatGeometry;
        }
        else if (app.Contains("discord"))
        {
            NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 101, 242));
            NotifBadgeIcon.Data = _discordGeometry;
        }
        else if (app.Contains("defender") || app.Contains("security") || app.Contains("antivirus"))
        {
            NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
            NotifBadgeIcon.Data = _shieldGeometry;
        }
        else
        {
            NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 100, 120));
            NotifBadgeIcon.Data = _bellGeometry;
        }

        NotifTitleText.Text = string.IsNullOrEmpty(notif.Title) ? notif.AppName : $"{notif.AppName} • {notif.Title}";
        NotifBodyText.Text = notif.Body;

        MorphTo(IslandVisualState.NotificationPopup);

        try
        {
            await Task.Delay(4500, token);

            if (!token.IsCancellationRequested)
            {
                if (_isHovered || _isLyricsExpanded)
                {
                    MorphTo(IslandVisualState.Expanded);
                }
                else
                {
                    MorphTo(IslandVisualState.Compact);
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    #endregion

    #region Album Color Extraction & Dynamic Ambient Gradient

    private void UpdateAlbumGradient(BitmapSource? bmp)
    {
        System.Windows.Media.Color targetColor;
        if (bmp == null || _currentMedia?.HasMedia != true)
        {
            targetColor = System.Windows.Media.Color.FromRgb(0, 0, 0);
        }
        else
        {
            targetColor = ExtractAccentColor(bmp);
        }

        var anim = new ColorAnimation
        {
            To = targetColor,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AlbumGradientStop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    private System.Windows.Media.Color ExtractAccentColor(BitmapSource? bmp)
    {
        if (bmp == null) return System.Windows.Media.Color.FromRgb(0, 0, 0);
        try
        {
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = formatted.PixelWidth;
            int h = formatted.PixelHeight;
            if (w <= 0 || h <= 0) return System.Windows.Media.Color.FromRgb(16, 26, 42);

            int stepX = Math.Max(1, w / 18);
            int stepY = Math.Max(1, h / 18);

            int stride = w * 4;
            byte[] pixels = new byte[h * stride];
            formatted.CopyPixels(pixels, stride, 0);

            long rSum = 0, gSum = 0, bSum = 0;
            int count = 0;
            double maxSaturation = -1.0;
            byte bestR = 20, bestG = 30, bestB = 50;

            for (int y = 0; y < h; y += stepY)
            {
                for (int x = 0; x < w; x += stepX)
                {
                    int idx = y * stride + x * 4;
                    if (idx + 3 >= pixels.Length) continue;

                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    if (a < 128) continue;

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    double sat = max == 0 ? 0 : (double)(max - min) / max;
                    double lum = (max + min) / 510.0;

                    if (lum > 0.12 && lum < 0.88 && sat > 0.15)
                    {
                        if (sat > maxSaturation)
                        {
                            maxSaturation = sat;
                            bestR = r;
                            bestG = g;
                            bestB = b;
                        }
                    }

                    rSum += r;
                    gSum += g;
                    bSum += b;
                    count++;
                }
            }

            if (maxSaturation > 0.15)
            {
                // Skala ambient glow agar teks tetap kontras tinggi
                byte finR = (byte)Math.Clamp((int)(bestR * 0.42), 0, 255);
                byte finG = (byte)Math.Clamp((int)(bestG * 0.42), 0, 255);
                byte finB = (byte)Math.Clamp((int)(bestB * 0.42), 0, 255);
                return System.Windows.Media.Color.FromRgb(finR, finG, finB);
            }
            else if (count > 0)
            {
                byte finR = (byte)Math.Clamp((int)((rSum / count) * 0.35), 0, 255);
                byte finG = (byte)Math.Clamp((int)((gSum / count) * 0.35), 0, 255);
                byte finB = (byte)Math.Clamp((int)((bSum / count) * 0.35), 0, 255);
                return System.Windows.Media.Color.FromRgb(finR, finG, finB);
            }
        }
        catch
        {
            // Fallback aman
        }
        return System.Windows.Media.Color.FromRgb(16, 26, 42);
    }

    #endregion

    #region Timeline Scrubber & Real-Time Lyrics Synchronization

    private void InitializePlaybackTimer()
    {
        _playbackTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _playbackTimer.Tick += (s, e) =>
        {
            if (_currentMedia?.HasMedia == true)
            {
                if (_mediaManager.TryGetLiveTimeline(out var livePos, out var liveDur, out var livePlaying))
                {
                    if (liveDur > TimeSpan.Zero)
                    {
                        _currentDuration = liveDur;
                    }
                    if (livePos > TimeSpan.Zero || !livePlaying)
                    {
                        _currentPosition = livePos;
                    }
                    else if (livePlaying && _currentDuration > TimeSpan.Zero && _currentPosition < _currentDuration)
                    {
                        _currentPosition += TimeSpan.FromMilliseconds(200);
                    }
                }
                else if (_currentMedia.IsPlaying && _currentDuration > TimeSpan.Zero && _currentPosition < _currentDuration)
                {
                    _currentPosition += TimeSpan.FromMilliseconds(200);
                }

                UpdateTimelineUI();
                SyncLyricsWithTimeline();
            }
        };
        _playbackTimer.Start();
    }

    private void UpdateTimelineUI()
    {
        if (_currentDuration > TimeSpan.Zero)
        {
            TrackPositionText.Text = _currentPosition.ToString(@"mm\:ss");
            var remaining = _currentDuration - _currentPosition;
            TrackDurationText.Text = remaining > TimeSpan.Zero ? $"-{remaining:mm\\:ss}" : "-00:00";

            double maxW = TrackProgressContainer.ActualWidth > 20 ? TrackProgressContainer.ActualWidth : 205.0;
            double pct = _currentPosition.TotalSeconds / _currentDuration.TotalSeconds;
            pct = Math.Clamp(pct, 0.0, 1.0);
            TrackProgressFill.Width = Math.Max(4, pct * maxW);
        }
        else
        {
            TrackPositionText.Text = "00:00";
            TrackDurationText.Text = "-00:00";
            TrackProgressFill.Width = 4;
        }
    }

    private void InitializeLyricsTimer()
    {
        // Sinkronisasi lirik kini dijalankan secara presisi (200ms) mengikuti timestamp lagu di InitializePlaybackTimer
    }

    private void OnLyricsLoaded(string key, LyricsResult result)
    {
        Dispatcher.Invoke(() =>
        {
            _syncedLyrics = new List<LyricItem>(result.SyncedLyrics);
            _plainLyrics = new List<string>(result.PlainLyrics);
            _currentLyricIndex = -2; // Paksa refresh tampilan lirik
            SyncLyricsWithTimeline();
        });
    }

    private void SyncLyricsWithTimeline()
    {
        if (_syncedLyrics.Count > 0)
        {
            // Lookahead 180ms agar transisi baris lirik terasa tepat saat vokal masuk
            TimeSpan targetTime = _currentPosition + TimeSpan.FromMilliseconds(180);
            int activeIdx = -1;

            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (_syncedLyrics[i].Time <= targetTime)
                {
                    activeIdx = i;
                }
                else
                {
                    break;
                }
            }

            if (activeIdx == _currentLyricIndex) return;
            _currentLyricIndex = activeIdx;

            if (activeIdx < 0)
            {
                // Masih di bagian intro sebelum lirik pertama masuk
                LyricLineAbove.Text = string.Empty;
                LyricLineCurrent.Text = "♪ • • •";
                LyricLineBelow.Text = _syncedLyrics[0].Text;
                LyricLineBelow2.Text = _syncedLyrics.Count > 1 ? _syncedLyrics[1].Text : string.Empty;
            }
            else
            {
                LyricLineAbove.Text = activeIdx > 0 ? _syncedLyrics[activeIdx - 1].Text : string.Empty;
                LyricLineCurrent.Text = _syncedLyrics[activeIdx].Text;
                LyricLineBelow.Text = (activeIdx + 1 < _syncedLyrics.Count) ? _syncedLyrics[activeIdx + 1].Text : string.Empty;
                LyricLineBelow2.Text = (activeIdx + 2 < _syncedLyrics.Count) ? _syncedLyrics[activeIdx + 2].Text : string.Empty;
            }
            return;
        }

        if (_plainLyrics.Count > 0)
        {
            int estIdx = 0;
            if (_currentDuration.TotalSeconds > 5 && _plainLyrics.Count > 1)
            {
                double ratio = Math.Clamp(_currentPosition.TotalSeconds / _currentDuration.TotalSeconds, 0.0, 0.999);
                estIdx = (int)(ratio * _plainLyrics.Count);
            }

            if (estIdx == _currentLyricIndex) return;
            _currentLyricIndex = estIdx;

            LyricLineAbove.Text = estIdx > 0 ? _plainLyrics[estIdx - 1] : string.Empty;
            LyricLineCurrent.Text = _plainLyrics[estIdx];
            LyricLineBelow.Text = (estIdx + 1 < _plainLyrics.Count) ? _plainLyrics[estIdx + 1] : string.Empty;
            LyricLineBelow2.Text = (estIdx + 2 < _plainLyrics.Count) ? _plainLyrics[estIdx + 2] : string.Empty;
            return;
        }

        LyricLineAbove.Text = string.Empty;
        LyricLineCurrent.Text = "♪ Menunggu lirik...";
        LyricLineBelow.Text = string.Empty;
        LyricLineBelow2.Text = string.Empty;
    }

    private void LyricsToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLyricsExpanded = !_isLyricsExpanded;
        UpdateLyricsToggleState();

        if (_isLyricsExpanded)
        {
            // Saat tombol lirik diklik ON: Dynamic Island otomatis tetap terbuka (pinned)
            MorphTo(IslandVisualState.Expanded);
        }
        else
        {
            // Saat tombol lirik diklik lagi (OFF): otomatis tutup kembali Dynamic Island
            MorphTo(IslandVisualState.Compact);
        }
    }

    private void UpdateLyricsToggleState()
    {
        if (_isLyricsExpanded)
        {
            LyricsExpandedPane.Visibility = Visibility.Visible;
            LyricsToggleBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 30, 215, 96));
            LyricsToggleIcon.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 215, 96));
            LyricsToggleBtn.ToolTip = "Tutup Lirik Lagu";
        }
        else
        {
            LyricsExpandedPane.Visibility = Visibility.Collapsed;
            LyricsToggleBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 255, 255, 255));
            LyricsToggleIcon.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(144, 255, 255, 255));
            LyricsToggleBtn.ToolTip = "Buka Lirik Lagu";
        }
    }

    #endregion

    #region Battery Status Dashboard (Halaman 2 Swipe - Laptop vs Standalone Device PC)

    private void UpdateBatteryPageUI(PowerInfo power)
    {
        double maxBarWidth = BatteryBarContainer.ActualWidth > 20 ? BatteryBarContainer.ActualWidth : 262.0;

        // Jika dijalankan di Desktop PC (tidak memiliki baterai fisik) -> "Standalone Device (PC)"
        if (!power.HasBattery)
        {
            var pcColor = System.Windows.Media.Color.FromRgb(56, 189, 248); // Cyan/Sky Blue Elegan
            BatteryStatusTitle.Text = "Standalone Device (PC)";
            BatteryStatusTitle.Foreground = new SolidColorBrush(pcColor);
            BatteryStatusSubtitle.Text = "Direct AC mains power supply";
            BatteryLargePercentText.Text = "AC";
            BatteryCenterIcon.Data = _boltGeometry;
            BatteryCenterIcon.Fill = new SolidColorBrush(pcColor);
            BatteryGaugeTrack.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 56, 189, 248));
            BatteryGaugeArc.Data = CreateBatteryArcGeometry(100, 17.0, 14.5);
            BatteryGaugeArc.Stroke = new SolidColorBrush(pcColor);
            BatteryHorizontalBar.Width = maxBarWidth;
            BatteryHorizontalBar.Background = new SolidColorBrush(pcColor);
            BatteryDetailLeft.Text = "Mode: Desktop PC";
            BatteryDetailRight.Text = "Power: Always On";
            return;
        }

        int pct = Math.Clamp(power.BatteryPercentage, 0, 100);
        BatteryLargePercentText.Text = $"{pct}%";

        double barW = Math.Max(6, (pct / 100.0) * maxBarWidth);
        BatteryHorizontalBar.Width = barW;
        BatteryGaugeArc.Data = CreateBatteryArcGeometry(pct, 17.0, 14.5);

        if (power.IsPluggedIn)
        {
            // Posisi sedang charging (Laptop)
            BatteryStatusTitle.Text = "Charging";
            BatteryStatusTitle.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
            BatteryStatusSubtitle.Text = "Power adapter connected";
            BatteryCenterIcon.Data = _boltGeometry;
            BatteryCenterIcon.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
            BatteryGaugeTrack.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 62, 40));
            BatteryGaugeArc.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
            BatteryHorizontalBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 209, 88));
            BatteryDetailLeft.Text = "Source: AC Power";
        }
        else
        {
            // Posisi TIDAK charging (Laptop) -> "Uncharged"
            BatteryStatusTitle.Text = "Uncharged";
            BatteryStatusTitle.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 235, 240));
            BatteryStatusSubtitle.Text = "Running on battery power";
            BatteryCenterIcon.Data = _batteryOutlineGeometry;

            System.Windows.Media.Color statusColor = pct <= 20
                ? System.Windows.Media.Color.FromRgb(255, 176, 32)
                : System.Windows.Media.Color.FromRgb(170, 180, 195);

            BatteryCenterIcon.Fill = new SolidColorBrush(statusColor);
            BatteryGaugeTrack.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
            BatteryGaugeArc.Stroke = new SolidColorBrush(statusColor);
            BatteryHorizontalBar.Background = new SolidColorBrush(statusColor);
            BatteryDetailLeft.Text = "Source: Battery";
        }

        BatteryDetailRight.Text = pct >= 80 ? "Health: Optimal" : "Health: Normal";
    }

    private static Geometry CreateBatteryArcGeometry(int percentage, double center, double radius)
    {
        double clamped = Math.Clamp(percentage, 2, 100);
        if (clamped >= 99.5) clamped = 99.5; // Agar arc lingkaran penuh tetap ter-render sempurna

        double angleRad = (clamped / 100.0) * 2.0 * Math.PI;
        double startX = center;
        double startY = center - radius;

        double endX = center + radius * Math.Sin(angleRad);
        double endY = center - radius * Math.Cos(angleRad);
        bool isLargeArc = clamped > 50.0;

        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(startX, startY),
            IsClosed = false
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new System.Windows.Point(endX, endY),
            Size = new System.Windows.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc
        });

        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        geom.Freeze();
        return geom;
    }

    #endregion

    #region AI Quick Prompt Page (Halaman 3 Swipe - Gemini, ChatGPT/Codex, Claude/Claude Code)

    private void RefreshOpenAiApps(bool adjustMorph = true)
    {
        bool hadApps = _openAiApps.Count > 0;
        _openAiApps = AiAppMonitor.GetOpenAiApps(_hWnd);

        if (_openAiApps.Count == 0)
        {
            // Tidak ada aplikasi AI terbuka -> Sembunyikan kolom input prompt (kosong)
            AiActivePanel.Visibility = Visibility.Collapsed;
            AiEmptyPanel.Visibility = Visibility.Visible;
            SetWindowActivatable(false);
        }
        else
        {
            // Ada aplikasi AI terbuka -> Munculkan kolom chat prompt
            AiActivePanel.Visibility = Visibility.Visible;
            AiEmptyPanel.Visibility = Visibility.Collapsed;
            SetWindowActivatable(true);

            if (_selectedAiAppIndex >= _openAiApps.Count || _selectedAiAppIndex < 0)
            {
                _selectedAiAppIndex = 0;
            }

            var currentAi = _openAiApps[_selectedAiAppIndex];
            UpdateAiBrandVisuals(currentAi);

            if (_openAiApps.Count > 1)
            {
                AiSwitchAppBtn.Visibility = Visibility.Visible;
                AiSwitchAppText.Text = $"{_selectedAiAppIndex + 1}/{_openAiApps.Count} ⟳";
            }
            else
            {
                AiSwitchAppBtn.Visibility = Visibility.Collapsed;
            }
        }

        if (adjustMorph && _currentState == IslandVisualState.Expanded && _activeExpandedPage == 2 && hadApps != (_openAiApps.Count > 0))
        {
            MorphTo(IslandVisualState.Expanded);
        }
    }

    private void UpdateAiBrandVisuals(DetectedAiApp app)
    {
        AiAppNameText.Text = app.DisplayName;
        AiAppStatusText.Text = "Ready • Ketik prompt lalu tekan Enter";
        AiPromptPlaceholder.Text = $"Tulis prompt untuk {app.DisplayName}...";

        System.Windows.Media.Color brandColor = app.Brand switch
        {
            AiBrand.Gemini => System.Windows.Media.Color.FromRgb(78, 130, 238),     // Google Gemini Blue
            AiBrand.ChatGpt or AiBrand.Codex => System.Windows.Media.Color.FromRgb(16, 163, 127), // OpenAI Emerald
            AiBrand.Claude or AiBrand.ClaudeCode => System.Windows.Media.Color.FromRgb(217, 119, 87), // Anthropic Terracotta
            _ => System.Windows.Media.Color.FromRgb(78, 130, 238)
        };

        AiBrandBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, brandColor.R, brandColor.G, brandColor.B));
        AiBrandBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(95, brandColor.R, brandColor.G, brandColor.B));
        AiBrandIcon.Fill = new SolidColorBrush(brandColor);
        AiSendPromptBtn.Background = new SolidColorBrush(brandColor);
    }

    private void AiSwitchAppBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_openAiApps.Count <= 1) return;
        _selectedAiAppIndex = (_selectedAiAppIndex + 1) % _openAiApps.Count;
        UpdateAiBrandVisuals(_openAiApps[_selectedAiAppIndex]);
        AiSwitchAppText.Text = $"{_selectedAiAppIndex + 1}/{_openAiApps.Count} ⟳";
    }

    private void AiInputBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ActivateAndFocusPromptBox();
    }

    private void AiPromptTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SetWindowActivatable(true);
        AiAppMonitor.ForceForegroundWindow(_hWnd);
    }

    private void ActivateAndFocusPromptBox()
    {
        SetWindowActivatable(true);
        AiAppMonitor.ForceForegroundWindow(_hWnd);
        Activate();
        AiPromptTextBox.Focus();
        Keyboard.Focus(AiPromptTextBox);
    }

    private void AiPromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AiPromptPlaceholder.Visibility = string.IsNullOrEmpty(AiPromptTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void AiPromptTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAiPromptAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            SetWindowActivatable(false);
        }
    }

    private async void AiSendPromptBtn_Click(object sender, RoutedEventArgs e)
    {
        await SubmitAiPromptAsync();
    }

    private async Task SubmitAiPromptAsync()
    {
        if (_isSendingPrompt || _openAiApps.Count == 0) return;

        string prompt = AiPromptTextBox.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        if (_selectedAiAppIndex >= _openAiApps.Count) _selectedAiAppIndex = 0;
        var targetApp = _openAiApps[_selectedAiAppIndex];

        _isSendingPrompt = true;
        AiPromptTextBox.Text = string.Empty;
        AiAppStatusText.Text = $"Mengirim prompt ke {targetApp.DisplayName}...";

        SetWindowActivatable(false);

        bool sent = await AiAppMonitor.SendPromptToAiAppAsync(targetApp, prompt);
        AiAppStatusText.Text = sent
            ? $"✓ Prompt terkirim ke {targetApp.DisplayName}!"
            : $"✓ Disalin ke Clipboard ({targetApp.DisplayName})";

        await Task.Delay(1800);
        _isSendingPrompt = false;
        if (_openAiApps.Count > 0 && _selectedAiAppIndex < _openAiApps.Count)
        {
            AiAppStatusText.Text = "Ready • Ketik prompt lalu tekan Enter";
        }
    }

    #endregion

    #region Swipe & Navigation Arrows Handler (3 Halaman: 0=Media, 1=Battery/PC, 2=AI Prompt)

    private void SwitchExpandedPage(int pageIndex)
    {
        _activeExpandedPage = ((pageIndex % 3) + 3) % 3;

        Page1Media.Visibility = _activeExpandedPage == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page2Battery.Visibility = _activeExpandedPage == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page3AiPrompt.Visibility = _activeExpandedPage == 2 ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new SolidColorBrush(Colors.White);
        var dimBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 255, 255, 255));

        PageDot1.Fill = _activeExpandedPage == 0 ? activeBrush : dimBrush;
        PageDot2.Fill = _activeExpandedPage == 1 ? activeBrush : dimBrush;
        PageDot3.Fill = _activeExpandedPage == 2 ? activeBrush : dimBrush;

        if (_activeExpandedPage == 1)
        {
            SetWindowActivatable(false);
            _currentPower = _powerMonitor.Refresh();
            UpdateBatteryPageUI(_currentPower);
        }
        else if (_activeExpandedPage == 2)
        {
            RefreshOpenAiApps(false);
        }
        else
        {
            SetWindowActivatable(false);
        }

        if (_currentState == IslandVisualState.Expanded)
        {
            MorphTo(IslandVisualState.Expanded);
        }
    }

    private void NavLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchExpandedPage(_activeExpandedPage - 1);
    }

    private void NavRightBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchExpandedPage(_activeExpandedPage + 1);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isHovered && _currentState == IslandVisualState.Expanded)
        {
            if (e.Delta < 0)
            {
                SwitchExpandedPage(_activeExpandedPage + 1); // Scroll down/kanan -> Halaman berikutnya
            }
            else if (e.Delta > 0)
            {
                SwitchExpandedPage(_activeExpandedPage - 1); // Scroll up/kiri -> Halaman sebelumnya
            }
        }
    }

    #endregion

    #region Audio Spectrum Wave Animation

    private void StartSpectrumAnimation()
    {
        if (_spectrumStoryboard != null) return;

        _spectrumStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        AddBarAnimation(SpecBar1, 5, 12, 260);
        AddBarAnimation(SpecBar2, 12, 5, 210);
        AddBarAnimation(SpecBar3, 6, 14, 280);
        AddBarAnimation(SpecBar4, 14, 4, 230);

        _spectrumStoryboard.Begin();
    }

    private void AddBarAnimation(Border bar, double fromH, double toH, int ms)
    {
        var anim = new DoubleAnimation
        {
            From = fromH,
            To = toH,
            Duration = TimeSpan.FromMilliseconds(ms),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, new PropertyPath(HeightProperty));
        _spectrumStoryboard!.Children.Add(anim);
    }

    private void StopSpectrumAnimation()
    {
        _spectrumStoryboard?.Stop();
        _spectrumStoryboard = null;

        SpecBar1.Height = 6;
        SpecBar2.Height = 11;
        SpecBar3.Height = 8;
        SpecBar4.Height = 13;
    }

    #endregion

    #region Hover Interactions

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovered = true;
        _currentPower = _powerMonitor.Refresh();
        if (_currentState is IslandVisualState.ChargingPopup or IslandVisualState.TwsPopup or IslandVisualState.NotificationPopup) return;

        SwitchExpandedPage(_activeExpandedPage);
        MorphTo(IslandVisualState.Expanded);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovered = false;
        if (_currentState is IslandVisualState.ChargingPopup or IslandVisualState.TwsPopup or IslandVisualState.NotificationPopup) return;

        // Jika mode baca lirik sedang aktif (tombol lirik diklik), Dynamic Island tetap terbuka terus
        // sampai tombol lirik diklik lagi oleh pengguna
        if (_isLyricsExpanded && _activeExpandedPage == 0)
        {
            return;
        }

        // Jika pengguna sedang aktif mengetik teks di kolom prompt AI, jangan tutup tiba-tiba
        if (_activeExpandedPage == 2 && AiPromptTextBox.IsKeyboardFocused && !string.IsNullOrEmpty(AiPromptTextBox.Text))
        {
            return;
        }

        Keyboard.ClearFocus();
        SetWindowActivatable(false);
        MorphTo(IslandVisualState.Compact);
    }

    #endregion

    #region Fluid Morphing Animation Engine (Spring Physics)

    private void MorphTo(IslandVisualState targetState)
    {
        _currentState = targetState;

        double targetWidth = 125;
        double targetHeight = 28;
        double targetBottomRadius = 14;

        FrameworkElement targetView = CompactView;

        switch (targetState)
        {
            case IslandVisualState.Compact:
                bool isPlaying = _currentMedia?.IsPlaying ?? false;
                bool isCharging = _currentPower.IsPluggedIn;

                if (isPlaying)
                {
                    targetWidth = 180;
                }
                else if (isCharging)
                {
                    targetWidth = 145;
                }
                else
                {
                    targetWidth = 115;
                }
                targetHeight = 28;
                targetBottomRadius = 14;
                targetView = CompactView;
                break;

            case IslandVisualState.ChargingPopup:
                targetWidth = 315;
                targetHeight = 48;
                targetBottomRadius = 20;
                targetView = ChargingPopupView;
                break;

            case IslandVisualState.TwsPopup:
                targetWidth = 315;
                targetHeight = 48;
                targetBottomRadius = 20;
                targetView = TwsPopupView;
                break;

            case IslandVisualState.NotificationPopup:
                targetWidth = 325;
                targetHeight = 48;
                targetBottomRadius = 20;
                targetView = NotificationPopupView;
                break;

            case IslandVisualState.Expanded:
                if (_activeExpandedPage == 1)
                {
                    // Halaman 2 (Battery / Standalone PC): Ukuran ringkas tanpa gap kosong
                    targetWidth = 320;
                    targetHeight = 96;
                }
                else if (_activeExpandedPage == 2)
                {
                    // Halaman 3 (AI Quick Prompt): Proporsional sesuai ada/tidaknya aplikasi AI yang terbuka
                    if (_openAiApps.Count > 0)
                    {
                        targetWidth = 345;
                        targetHeight = 104;
                    }
                    else
                    {
                        targetWidth = 295;
                        targetHeight = 82;
                    }
                }
                else if (_isLyricsExpanded)
                {
                    // Halaman 1 (Media + Lirik Bersebelahan)
                    targetWidth = 548;
                    targetHeight = 135;
                }
                else
                {
                    // Halaman 1 (Media Saja)
                    targetWidth = 340;
                    targetHeight = 135;
                }
                targetBottomRadius = 20;
                targetView = ExpandedContainerView;
                break;
        }

        var springEase = (IEasingFunction)Resources["SpringEaseOut"];
        var smoothEase = (IEasingFunction)Resources["SmoothEaseOut"];
        var duration = TimeSpan.FromMilliseconds(350);

        double currentW = IslandBorder.ActualWidth > 0 ? IslandBorder.ActualWidth : IslandBorder.Width;
        double currentH = IslandBorder.ActualHeight > 0 ? IslandBorder.ActualHeight : IslandBorder.Height;
        double currentR = ((CornerRadius)IslandBorder.GetValue(Border.CornerRadiusProperty)).BottomLeft;

        var animWidth = new DoubleAnimation
        {
            From = currentW,
            To = targetWidth,
            Duration = duration,
            EasingFunction = springEase
        };

        var animHeight = new DoubleAnimation
        {
            From = currentH,
            To = targetHeight,
            Duration = duration,
            EasingFunction = springEase
        };

        var animRadius = new BottomCornerRadiusAnimation
        {
            From = currentR,
            To = targetBottomRadius,
            Duration = duration,
            EasingFunction = smoothEase
        };

        IslandBorder.BeginAnimation(WidthProperty, animWidth);
        IslandBorder.BeginAnimation(HeightProperty, animHeight);
        IslandBorder.BeginAnimation(Border.CornerRadiusProperty, animRadius);

        AnimatePanelOpacity(CompactView, targetView == CompactView);
        AnimatePanelOpacity(ChargingPopupView, targetView == ChargingPopupView);
        AnimatePanelOpacity(TwsPopupView, targetView == TwsPopupView);
        AnimatePanelOpacity(NotificationPopupView, targetView == NotificationPopupView);
        AnimatePanelOpacity(ExpandedContainerView, targetView == ExpandedContainerView);
    }

    private void AnimatePanelOpacity(FrameworkElement view, bool show)
    {
        double toOpacity = show ? 1.0 : 0.0;

        if (show)
        {
            if (view.Visibility != Visibility.Visible)
            {
                view.Opacity = 0.0;
                view.Visibility = Visibility.Visible;
            }
        }

        var anim = new DoubleAnimation
        {
            From = view.Opacity,
            To = toOpacity,
            Duration = TimeSpan.FromMilliseconds(show ? 250 : 120),
            EasingFunction = (IEasingFunction)Resources["SmoothEaseOut"]
        };

        if (!show)
        {
            anim.Completed += (s, e) =>
            {
                if (view.Opacity <= 0.05)
                {
                    view.Visibility = Visibility.Collapsed;
                }
            };
        }

        view.BeginAnimation(OpacityProperty, anim);
    }

    #endregion

    #region Media Controls Button Click

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaManager.PlayPauseAsync();
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaManager.PreviousTrackAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await _mediaManager.NextTrackAsync();
    }

    #endregion

    #region System Tray & Context Menus

    private void InitializeTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon();
        try
        {
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
            }
            else
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _notifyIcon.Text = "Dynamic Island (Windows 11)";
        _notifyIcon.Visible = true;

        var contextMenu = new System.Windows.Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings", null, (s, e) =>
        {
            Dispatcher.Invoke(OpenSettingsPopup);
        });
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (s, e) =>
        {
            Dispatcher.Invoke(CloseApplication);
        });
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsPopup();
    }

    private void OpenSettingsPopup()
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseApplication();
    }

    private void CloseApplication()
    {
        _settingsWindow?.Close();
        _notifMonitor.Dispose();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        System.Windows.Application.Current?.Shutdown();
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _settingsWindow?.Close();
        _clockTimer?.Stop();
        _playbackTimer?.Stop();
        StopSpectrumAnimation();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _notificationCts?.Cancel();
        _mediaManager.Dispose();
        _powerMonitor.Dispose();
        _audioMonitor.Dispose();
        _notifMonitor.Dispose();
        _lyricsService.Dispose();
        base.OnClosed(e);
    }
}

public class BottomCornerRadiusAnimation : AnimationTimeline
{
    public double? From { get; set; }
    public double? To { get; set; }
    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(CornerRadius);

    protected override Freezable CreateInstanceCore() => new BottomCornerRadiusAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double progress = animationClock.CurrentProgress ?? 0.0;
        if (EasingFunction != null)
        {
            progress = EasingFunction.Ease(progress);
        }

        CornerRadius current = (CornerRadius)defaultOriginValue;
        double fromVal = From ?? current.BottomLeft;
        double toVal = To ?? current.BottomLeft;

        double bottomVal = fromVal + (toVal - fromVal) * progress;
        return new CornerRadius(0, 0, bottomVal, bottomVal);
    }
}