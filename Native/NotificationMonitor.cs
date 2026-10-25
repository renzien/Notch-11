using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Xml.Linq;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Native;

public record AppNotificationInfo(string AppName, string Title, string Body, string AppId);

public class NotificationMonitor : IDisposable
{
    private UserNotificationListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly HashSet<uint> _seenWinRtIds = new();
    private bool _winRtSeeded;

    private long _lastWpnOrder = -1;
    private ulong _lastShmWalHeaderHash = 0;
    private DateTime _lastForcedDbCheck = DateTime.MinValue;

    // Deduplikasi agar notifikasi yang sama dari beberapa layer hanya tampil 1 kali
    private readonly Dictionary<string, DateTime> _recentSignatures = new();
    private readonly object _dedupLock = new();

    // Pelacakan popup / window title untuk LINE & WhatsApp Desktop
    private readonly HashSet<IntPtr> _seenLinePopupHwnds = new();
    private string _lastLineWindowTitle = string.Empty;
    private string _lastWhatsAppWindowTitle = string.Empty;

    // Hook & pelacakan koordinat asli untuk menyembunyikan default Windows Toast Banner selama Dynamic Island aktif
    private IntPtr _winEventHook = IntPtr.Zero;
    private WinEventDelegate? _winEventProc;
    private readonly Dictionary<IntPtr, (int Left, int Top)> _suppressedWindowCoords = new();
    private readonly object _suppressLock = new();
    private bool _isDisposed;

    public event Action<AppNotificationInfo>? NotificationReceived;
    public UserNotificationListenerAccessStatus AccessStatus { get; private set; } = UserNotificationListenerAccessStatus.Unspecified;

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();

        // Pasang WinEventHook di UI thread untuk langsung menyembunyikan banner notifikasi bawaan Windows (<1ms)
        InstallToastSuppressorHook();

        // Layer 1: WinRT UserNotificationListener
        try
        {
            _listener = UserNotificationListener.Current;
            if (_listener != null)
            {
                AccessStatus = await _listener.RequestAccessAsync();
                Debug.WriteLine($"NotificationListener AccessStatus: {AccessStatus}");

                try
                {
                    _listener.NotificationChanged += OnNotificationChanged;
                }
                catch
                {
                    // Unpackaged Win32 app sering melempar 0x80070490 pada event subscription.
                    // Akan ditangani oleh polling GetNotificationsAsync & WPN SQLite Reader di bawah.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NotificationListener init: {ex.Message}");
        }

        // Seed state awal agar notifikasi lama tidak muncul ulang saat aplikasi baru dijalankan
        await SeedInitialStateAsync();

        // Jalankan background monitor loop
        _ = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    private async Task SeedInitialStateAsync()
    {
        // 1. Seed WinRT Toast IDs yang sudah ada di Action Center
        if (_listener != null)
        {
            try
            {
                var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (existing != null)
                {
                    lock (_seenWinRtIds)
                    {
                        foreach (var n in existing)
                        {
                            _seenWinRtIds.Add(n.Id);
                        }
                    }
                    _winRtSeeded = true;
                }
            }
            catch { }
        }

        // 2. Seed WPN SQLite Database MAX(Order) & Header Hash
        try
        {
            string notifDir = GetWpnDirectory();
            string dbPath = Path.Combine(notifDir, "wpndatabase.db");
            if (File.Exists(dbPath))
            {
                _lastShmWalHeaderHash = ComputeShmAndWalHeaderHash(notifDir);
                _lastWpnOrder = GetMaxWpnNotificationOrder(dbPath);
                _lastForcedDbCheck = DateTime.UtcNow;
            }
        }
        catch { }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Layer 1: Poll WinRT UserNotificationListener
                await PollWinRtNotificationsAsync();

                // Layer 2: Cek Live Toast Banner Windows 11 via UI Automation (CoreWindow)
                PollLiveToastBannerWindow();

                // Layer 3: Cek Windows Push Notification SQLite Database (wpndatabase.db + WAL + SHM)
                PollWpnDatabase();

                // Layer 4: Cek popup/badge khusus LINE & WhatsApp Desktop
                PollDesktopChatWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MonitorLoop error: {ex.Message}");
            }

            try
            {
                await Task.Delay(450, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    #region Layer 1: WinRT UserNotificationListener

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            try
            {
                var notif = sender.GetNotification(args.UserNotificationId);
                if (notif != null)
                {
                    lock (_seenWinRtIds)
                    {
                        _seenWinRtIds.Add(notif.Id);
                    }
                    ProcessWinRtNotification(notif);
                }
            }
            catch { }
        }
    }

    private async Task PollWinRtNotificationsAsync()
    {
        if (_listener == null) return;

        try
        {
            var notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (notifs == null) return;

            if (!_winRtSeeded)
            {
                lock (_seenWinRtIds)
                {
                    foreach (var n in notifs) _seenWinRtIds.Add(n.Id);
                }
                _winRtSeeded = true;
                return;
            }

            foreach (var notif in notifs)
            {
                bool isNew;
                lock (_seenWinRtIds)
                {
                    isNew = _seenWinRtIds.Add(notif.Id);
                }

                if (isNew)
                {
                    var age = DateTimeOffset.UtcNow - notif.CreationTime;
                    if (age.TotalSeconds <= 20)
                    {
                        ProcessWinRtNotification(notif);
                    }
                }
            }
        }
        catch { }
    }

    private void ProcessWinRtNotification(UserNotification notif)
    {
        try
        {
            string appName = string.Empty;
            string appId = string.Empty;
            try
            {
                appName = notif.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
                appId = notif.AppInfo?.Id ?? string.Empty;
            }
            catch { }

            var texts = new List<string>();
            if (notif.Notification?.Visual?.Bindings != null)
            {
                foreach (var binding in notif.Notification.Visual.Bindings)
                {
                    var elements = binding.GetTextElements();
                    if (elements != null)
                    {
                        foreach (var el in elements)
                        {
                            if (!string.IsNullOrWhiteSpace(el.Text))
                            {
                                texts.Add(el.Text.Trim());
                            }
                        }
                    }
                    if (texts.Count > 0) break;
                }
            }

            EmitNormalizedNotification(appName, appId, texts);
        }
        catch { }
    }

    #endregion

    #region Layer 2: Windows Push Notification SQLite Database (wpndatabase.db + WAL + SHM)

    private static string GetWpnDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Microsoft", "Windows", "Notifications");
    }

    /// <summary>
    /// Membaca 128 byte pertama dari wpndatabase.db-shm (WalIndexHdr yang berubah di RAM setiap transaksi SQLite)
    /// dan 32 byte pertama dari wpndatabase.db-wal. Cara ini tidak terkena delay cache metadata NTFS.
    /// </summary>
    private static ulong ComputeShmAndWalHeaderHash(string notifDir)
    {
        ulong hash = 14695981039346656037UL;
        string shmPath = Path.Combine(notifDir, "wpndatabase.db-shm");
        string walPath = Path.Combine(notifDir, "wpndatabase.db-wal");

        Span<byte> buf = stackalloc byte[128];

        if (File.Exists(shmPath))
        {
            try
            {
                using var fs = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = fs.Read(buf);
                for (int i = 0; i < read; i++)
                {
                    hash ^= buf[i];
                    hash *= 1099511628211UL;
                }
            }
            catch { }
        }

        if (File.Exists(walPath))
        {
            try
            {
                using var fs = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = fs.Read(buf.Slice(0, 32));
                for (int i = 0; i < read; i++)
                {
                    hash ^= buf[i];
                    hash *= 1099511628211UL;
                }
                hash ^= (ulong)fs.Length;
                hash *= 1099511628211UL;
            }
            catch { }
        }

        return hash;
    }

    private void PollWpnDatabase()
    {
        string notifDir = GetWpnDirectory();
        string dbPath = Path.Combine(notifDir, "wpndatabase.db");
        if (!File.Exists(dbPath)) return;

        ulong currentHash = ComputeShmAndWalHeaderHash(notifDir);
        bool hashChanged = (currentHash != _lastShmWalHeaderHash);
        bool periodicCheck = (DateTime.UtcNow - _lastForcedDbCheck).TotalSeconds >= 2.0;

        if (hashChanged || periodicCheck)
        {
            _lastShmWalHeaderHash = currentHash;
            _lastForcedDbCheck = DateTime.UtcNow;

            if (_lastWpnOrder < 0)
            {
                _lastWpnOrder = GetMaxWpnNotificationOrder(dbPath);
            }
            else
            {
                QueryNewWpnNotifications(dbPath);
            }
        }
    }

    private long GetMaxWpnNotificationOrder(string dbPath)
    {
        return ExecuteWithSnapshotDb(dbPath, db =>
        {
            long maxOrder = 0;
            string sql = "SELECT IFNULL(MAX([Order]), 0) FROM Notification;";
            if (PrepareStmt(db, sql, out IntPtr stmt) == 0 && stmt != IntPtr.Zero)
            {
                try
                {
                    if (WinSqlite3.sqlite3_step(stmt) == WinSqlite3.SQLITE_ROW)
                    {
                        maxOrder = WinSqlite3.sqlite3_column_int64(stmt, 0);
                    }
                }
                finally
                {
                    WinSqlite3.sqlite3_finalize(stmt);
                }
            }
            return maxOrder;
        }, -1L);
    }

    private void QueryNewWpnNotifications(string dbPath)
    {
        ExecuteWithSnapshotDb(dbPath, db =>
        {
            string sql = $"SELECT n.[Order], IFNULL(nh.PrimaryId, ''), IFNULL(n.Type, ''), n.Payload FROM Notification n LEFT JOIN NotificationHandler nh ON n.HandlerId = nh.RecordId WHERE n.[Order] > {_lastWpnOrder} ORDER BY n.[Order] ASC;";
            if (PrepareStmt(db, sql, out IntPtr stmt) == 0 && stmt != IntPtr.Zero)
            {
                try
                {
                    while (WinSqlite3.sqlite3_step(stmt) == WinSqlite3.SQLITE_ROW)
                    {
                        long order = WinSqlite3.sqlite3_column_int64(stmt, 0);
                        string primaryId = GetColumnString(stmt, 1);
                        string notifType = GetColumnString(stmt, 2);
                        string payloadXml = GetColumnString(stmt, 3);

                        if (order > _lastWpnOrder)
                        {
                            _lastWpnOrder = order;
                        }

                        if (!string.IsNullOrWhiteSpace(payloadXml) &&
                            (notifType.Equals("toast", StringComparison.OrdinalIgnoreCase) ||
                             payloadXml.Contains("<toast", StringComparison.OrdinalIgnoreCase) ||
                             payloadXml.Contains("<binding", StringComparison.OrdinalIgnoreCase)))
                        {
                            ParseAndEmitToastXml(payloadXml, primaryId);
                        }
                    }
                }
                finally
                {
                    WinSqlite3.sqlite3_finalize(stmt);
                }
            }
            return 0;
        }, 0);
    }

    /// <summary>
    /// WpnUserService di Windows 11 membuka wpndatabase.db dengan PRAGMA locking_mode=EXCLUSIVE.
    /// Dengan menyalin wpndatabase.db + wpndatabase.db-wal menggunakan FileShare.ReadWrite ke cache temp
    /// (tanpa file -shm lama), winsqlite3.dll dapat me-replay seluruh frame WAL secara instan tanpa terkena SQLITE_BUSY.
    /// </summary>
    private static T ExecuteWithSnapshotDb<T>(string dbPath, Func<IntPtr, T> action, T fallback)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DynamicIsland_WpnSnapshot");
        try
        {
            Directory.CreateDirectory(tempDir);
            string tempDb = Path.Combine(tempDir, "wpndatabase.db");
            string tempWal = tempDb + "-wal";
            string tempShm = tempDb + "-shm";

            // Hapus -shm lama agar SQLite merekonstruksi index dari file -wal terbaru
            if (File.Exists(tempShm))
            {
                try { File.Delete(tempShm); } catch { }
            }

            CopySharedFile(dbPath, tempDb);

            string walPath = dbPath + "-wal";
            if (File.Exists(walPath))
            {
                CopySharedFile(walPath, tempWal);
            }
            else if (File.Exists(tempWal))
            {
                try { File.Delete(tempWal); } catch { }
            }

            byte[] tempBytes = Encoding.UTF8.GetBytes(tempDb + "\0");
            if (WinSqlite3.sqlite3_open_v2(tempBytes, out IntPtr tDb, WinSqlite3.SQLITE_OPEN_READWRITE | WinSqlite3.SQLITE_OPEN_NOMUTEX, IntPtr.Zero) == 0 && tDb != IntPtr.Zero)
            {
                try
                {
                    WinSqlite3.sqlite3_busy_timeout(tDb, 100);
                    return action(tDb);
                }
                finally
                {
                    WinSqlite3.sqlite3_close_v2(tDb);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ExecuteWithSnapshotDb error: {ex.Message}");
        }

        return fallback;
    }

    private static void CopySharedFile(string source, string dest)
    {
        using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        srcStream.CopyTo(dstStream);
    }

    private static int PrepareStmt(IntPtr db, string sql, out IntPtr stmt)
    {
        byte[] sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
        return WinSqlite3.sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out stmt, IntPtr.Zero);
    }

    private static string GetColumnString(IntPtr stmt, int col)
    {
        int len = WinSqlite3.sqlite3_column_bytes(stmt, col);
        if (len <= 0) return string.Empty;
        IntPtr ptr = WinSqlite3.sqlite3_column_blob(stmt, col);
        if (ptr == IntPtr.Zero) return string.Empty;
        byte[] buf = new byte[len];
        Marshal.Copy(ptr, buf, 0, len);
        return Encoding.UTF8.GetString(buf).Trim('\0');
    }

    private void ParseAndEmitToastXml(string xml, string appHint)
    {
        var texts = new List<string>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var t in doc.Descendants().Where(e => e.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase)))
            {
                // Abaikan text placement="attribution" jika sudah ada judul & isi utama
                string placement = t.Attribute("placement")?.Value ?? string.Empty;
                if (placement.Equals("attribution", StringComparison.OrdinalIgnoreCase)) continue;

                string val = t.Value?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(val))
                {
                    texts.Add(val);
                }
            }
        }
        catch
        {
            var matches = Regex.Matches(xml, @"<text[^>]*>(.*?)</text>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in matches)
            {
                string val = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    texts.Add(val);
                }
            }
        }

        if (texts.Count == 0) return;

        EmitNormalizedNotification(string.Empty, appHint + " " + xml, texts);
    }

    #endregion

    #region Layer 2: Live Windows 11 Toast Banner UI Automation Reader & Native Suppressor (CoreWindow)

    private void InstallToastSuppressorHook()
    {
        try
        {
            _winEventProc = OnWinEvent;
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_CREATE,
                EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InstallToastSuppressorHook error: {ex.Message}");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || idObject != OBJID_WINDOW || hWnd == IntPtr.Zero) return;
        if (eventType != EVENT_OBJECT_SHOW && eventType != EVENT_OBJECT_CREATE && eventType != EVENT_OBJECT_LOCATIONCHANGE) return;

        try
        {
            if (!IsWindowVisible(hWnd)) return;

            string className = GetClassNameString(hWnd);
            if (className.Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            {
                if (IsToastBannerCoreWindow(hWnd, out RECT rect))
                {
                    SuppressBannerWindow(hWnd, rect);

                    // Baca isi teks toast secara asinkron agar langsung tampil di Dynamic Island
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(60);
                        ReadAndEmitFromCoreWindow(hWnd);
                        PollWpnDatabase();
                    });
                }
            }
        }
        catch { }
    }

    private static bool IsToastBannerCoreWindow(IntPtr hWnd, out RECT rect)
    {
        rect = default;
        if (!IsWindowVisible(hWnd)) return false;
        if (!GetWindowRect(hWnd, out rect)) return false;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        // Ukuran banner toast standar Windows 11 (lebar ~364-450px, tinggi ~80-420px)
        if (w < 220 || w > 520 || h < 50 || h > 440) return false;

        string winTitle = GetWindowTextString(hWnd);

        // Pastikan BUKAN Notification Center (Win+N), Quick Settings (Win+A), Start Menu, atau Search
        if (winTitle.Contains("Center", StringComparison.OrdinalIgnoreCase) ||
            winTitle.Contains("Pusat", StringComparison.OrdinalIgnoreCase) ||
            winTitle.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
            winTitle.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
            winTitle.Contains("Pencarian", StringComparison.OrdinalIgnoreCase) ||
            winTitle.Contains("Quick", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool isToastTitle = winTitle.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                            winTitle.Contains("notifikasi", StringComparison.OrdinalIgnoreCase) ||
                            winTitle.Contains("pemberitahuan", StringComparison.OrdinalIgnoreCase);

        return isToastTitle;
    }

    private void SuppressBannerWindow(IntPtr hWnd, RECT rect)
    {
        if (_isDisposed || hWnd == IntPtr.Zero) return;

        lock (_suppressLock)
        {
            // Simpan koordinat asli hanya ketika window masih berada di area layar normal
            if (rect.Left > -10000 && rect.Top > -10000)
            {
                _suppressedWindowCoords[hWnd] = (rect.Left, rect.Top);
            }
        }

        if (rect.Left > -10000)
        {
            // Pindahkan ke luar layar (-32000, -32000) tanpa mengubah ukuran/fokus agar tidak tampil di pojok kanan bawah
            // tetapi tetap hidup bagi UIAutomation & WpnUserService
            SetWindowPos(
                hWnd,
                IntPtr.Zero,
                -32000,
                -32000,
                0,
                0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS);
        }
    }

    private void RestoreSuppressedBanners()
    {
        lock (_suppressLock)
        {
            foreach (var kvp in _suppressedWindowCoords)
            {
                try
                {
                    if (kvp.Key != IntPtr.Zero)
                    {
                        SetWindowPos(
                            kvp.Key,
                            IntPtr.Zero,
                            kvp.Value.Left,
                            kvp.Value.Top,
                            0,
                            0,
                            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS);
                    }
                }
                catch { }
            }
            _suppressedWindowCoords.Clear();
        }
    }

    private void ReadAndEmitFromCoreWindow(IntPtr hWnd)
    {
        try
        {
            var texts = ExtractUiAutomationTexts(hWnd);
            if (texts.Count > 0)
            {
                EmitNormalizedNotification(string.Empty, string.Join(" ", texts), texts);
            }
        }
        catch { }
    }

    private void PollLiveToastBannerWindow()
    {
        if (_isDisposed) return;

        try
        {
            IntPtr hWnd = IntPtr.Zero;
            while ((hWnd = FindWindowEx(IntPtr.Zero, hWnd, "Windows.UI.Core.CoreWindow", null)) != IntPtr.Zero)
            {
                if (!IsToastBannerCoreWindow(hWnd, out RECT rect)) continue;

                // Sembunyikan banner default Windows jika masih di area layar
                SuppressBannerWindow(hWnd, rect);

                // Ekstrak teks notifikasi untuk ditampilkan di Dynamic Island
                ReadAndEmitFromCoreWindow(hWnd);
            }
        }
        catch { }
    }

    private static List<string> ExtractUiAutomationTexts(IntPtr hWnd)
    {
        var result = new List<string>();
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null) return result;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            var elements = root.FindAll(TreeScope.Descendants, cond);
            if (elements == null) return result;

            foreach (AutomationElement el in elements)
            {
                try
                {
                    string name = el.Current.Name?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Abaikan teks tombol sistem pada banner toast
                    if (name.Equals("Dismiss", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Close", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Tutup", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Send", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Kirim", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Reply", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Balas", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(" Snooze", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!result.Contains(name))
                    {
                        result.Add(name);
                    }
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    #endregion

    #region Layer 3 & 4: LINE & WhatsApp Desktop Window Popup / Unread Badge Detector

    private void PollDesktopChatWindows()
    {
        try
        {
            // Bersihkan handle popup LINE yang sudah tertutup agar popup berikutnya terdeteksi lagi
            _seenLinePopupHwnds.RemoveWhere(h => !IsWindowVisible(h));

            // 1. Cek proses LINE.exe (Popup bawaan LINE atau perubahan angka unread pada judul window)
            var lineProcs = Process.GetProcessesByName("LINE");
            foreach (var proc in lineProcs)
            {
                try
                {
                    string mainTitle = proc.MainWindowTitle ?? string.Empty;
                    if (!string.IsNullOrEmpty(mainTitle) && mainTitle != _lastLineWindowTitle)
                    {
                        var unreadMatch = Regex.Match(mainTitle, @"LINE\s*\((\d+)\)", RegexOptions.IgnoreCase);
                        if (unreadMatch.Success && !string.IsNullOrEmpty(_lastLineWindowTitle))
                        {
                            EmitNormalizedNotification("LINE", "LINE", new List<string> { "LINE Messenger", $"{unreadMatch.Groups[1].Value} pesan baru diterima" });
                        }
                        _lastLineWindowTitle = mainTitle;
                    }

                    uint linePid = (uint)proc.Id;
                    EnumWindows((hWnd, lParam) =>
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (pid == linePid && IsWindowVisible(hWnd) && hWnd != proc.MainWindowHandle)
                        {
                            if (GetWindowRect(hWnd, out RECT rect))
                            {
                                int w = rect.Right - rect.Left;
                                int h = rect.Bottom - rect.Top;
                                // Ukuran khas popup notifikasi LINE di pojok layar
                                if (w >= 180 && w <= 480 && h >= 45 && h <= 220)
                                {
                                    if (_seenLinePopupHwnds.Add(hWnd))
                                    {
                                        var uiaTexts = ExtractUiAutomationTexts(hWnd);
                                        if (uiaTexts.Count > 0)
                                        {
                                            EmitNormalizedNotification("LINE", "LINE", uiaTexts);
                                        }
                                        else
                                        {
                                            string popupTitle = GetWindowTextString(hWnd);
                                            string msg = string.IsNullOrWhiteSpace(popupTitle) || popupTitle.Equals("LINE", StringComparison.OrdinalIgnoreCase)
                                                ? "Pesan baru masuk di LINE"
                                                : popupTitle;
                                            EmitNormalizedNotification("LINE", "LINE", new List<string> { "LINE Messenger", msg });
                                        }
                                    }

                                    // Sembunyikan popup bawaan LINE di pojok layar selama Dynamic Island aktif
                                    SuppressBannerWindow(hWnd, rect);
                                }
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            // 2. Cek proses WhatsApp.exe jika ada perubahan unread counter pada judul window
            var waProcs = Process.GetProcessesByName("WhatsApp");
            foreach (var proc in waProcs)
            {
                try
                {
                    string mainTitle = proc.MainWindowTitle ?? string.Empty;
                    if (!string.IsNullOrEmpty(mainTitle) && mainTitle != _lastWhatsAppWindowTitle)
                    {
                        var unreadMatch = Regex.Match(mainTitle, @"\((\d+)\)\s*WhatsApp|WhatsApp\s*\((\d+)\)", RegexOptions.IgnoreCase);
                        if (unreadMatch.Success && !string.IsNullOrEmpty(_lastWhatsAppWindowTitle))
                        {
                            string count = !string.IsNullOrEmpty(unreadMatch.Groups[1].Value) ? unreadMatch.Groups[1].Value : unreadMatch.Groups[2].Value;
                            EmitNormalizedNotification("WhatsApp", "WhatsApp", new List<string> { "WhatsApp", $"{count} pesan belum dibaca" });
                        }
                        _lastWhatsAppWindowTitle = mainTitle;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { }
    }

    #endregion

    #region Normalization & Deduplication

    private void EmitNormalizedNotification(string rawAppName, string appHint, List<string> texts)
    {
        if (texts.Count == 0) return;

        string combinedHint = $"{rawAppName} {appHint} {string.Join(" ", texts)}";

        string appName = rawAppName;
        if (combinedHint.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            appName = "WhatsApp";
        }
        else if (combinedHint.Contains("LINE", StringComparison.OrdinalIgnoreCase) ||
                 combinedHint.Contains("naver", StringComparison.OrdinalIgnoreCase))
        {
            appName = "LINE";
        }
        else if (combinedHint.Contains("Discord", StringComparison.OrdinalIgnoreCase))
        {
            appName = "Discord";
        }
        else if (combinedHint.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
                 combinedHint.Contains("Windows.SystemToast.Security", StringComparison.OrdinalIgnoreCase) ||
                 combinedHint.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase))
        {
            appName = "Windows Security";
        }
        else if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "Notification";
        }

        string title;
        string body;

        if (texts.Count >= 3 && texts[0].Equals(appName, StringComparison.OrdinalIgnoreCase))
        {
            title = texts[1];
            body = string.Join(" • ", texts.Skip(2));
        }
        else if (texts.Count >= 2)
        {
            if (texts[0].Equals(appName, StringComparison.OrdinalIgnoreCase))
            {
                title = appName;
                body = texts[1];
            }
            else
            {
                title = texts[0];
                body = string.Join(" • ", texts.Skip(1));
            }
        }
        else
        {
            title = appName;
            body = texts[0];
        }

        string signature = $"{appName}|{title}|{body}".ToLowerInvariant();
        DateTime now = DateTime.UtcNow;

        lock (_dedupLock)
        {
            var expired = _recentSignatures.Where(kv => (now - kv.Value).TotalSeconds > 8.0).Select(kv => kv.Key).ToList();
            foreach (var k in expired) _recentSignatures.Remove(k);

            if (_recentSignatures.ContainsKey(signature))
            {
                _recentSignatures[signature] = now;
                return;
            }

            _recentSignatures[signature] = now;
        }

        NotificationReceived?.Invoke(new AppNotificationInfo(appName, title, body, appName));
    }

    #endregion

    public void TriggerManualTest(string appName, string title, string message)
    {
        NotificationReceived?.Invoke(new AppNotificationInfo(appName, title, message, appName));
    }

    public void OpenWindowsNotificationSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Cancel();

        if (_winEventHook != IntPtr.Zero)
        {
            try
            {
                UnhookWinEvent(_winEventHook);
            }
            catch { }
            _winEventHook = IntPtr.Zero;
        }

        // Kembalikan posisi container Toast Notification Windows ke posisi default agar saat Dynamic Island ditutup,
        // notifikasi bawaan Windows langsung muncul normal kembali
        RestoreSuppressedBanners();

        if (_listener != null)
        {
            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
            }
            catch { }
        }
    }

    #region Native Win32 & WinSQLite3 Interop

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static string GetWindowTextString(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string GetClassNameString(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static class WinSqlite3
    {
        public const int SQLITE_OPEN_READONLY = 0x00000001;
        public const int SQLITE_OPEN_READWRITE = 0x00000002;
        public const int SQLITE_OPEN_NOMUTEX = 0x00008000;
        public const int SQLITE_ROW = 100;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_step(IntPtr pStmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern long sqlite3_column_int64(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr sqlite3_column_blob(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_column_bytes(IntPtr pStmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_finalize(IntPtr pStmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_close_v2(IntPtr db);
    }

    #endregion
}