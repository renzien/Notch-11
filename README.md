# Notch 11

A lightweight, native Dynamic Island notch for Windows 11 built with C# (.NET 8 / WPF) and Win32 APIs.

I built this because most desktop widgets on Windows are Electron wrappers that eat 200–400 MB of RAM just to show a media bar. This runs natively on top of WPF and Win32 interop, idles at ~0% CPU, stays under 30 MB of RAM, and doesn't steal window focus when you click it.

---

## What It Does

- **Idle & Music Compact Notch**
  - Sits flush at the top-center of your primary screen (`WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`), hidden from `Alt+Tab` and the Taskbar.
  - Shows a clean digital clock (`HH:mm`) when idle.
  - Automatically morphs when music plays on Spotify (or any Windows media session), showing a music badge, scrolling track title, and animated audio spectrum bars.

- **Page 1: Media Controls & Synced Lyrics**
  - Hover over the notch to expand full playback controls (Play/Pause, Next, Previous, live timeline scrubber).
  - Extracts the dominant color from the current album cover and applies a subtle ambient gradient to the notch background.
  - Click the lyrics button to expand the side-by-side live lyrics panel, synced in real time via LRCLIB.

- **Page 2: Battery & Power Status**
  - Use the `<` and `>` arrows on hover to switch pages.
  - Shows real-time battery percentage, charging state (`Charging` / `Uncharged`), and power gauges directly synced with the Windows kernel (`GetSystemPowerStatus`).
  - Automatically detects desktop PCs without a battery and switches to `Standalone Device (PC)` mode.
  - Pops up automatically when you plug in your charger or connect Bluetooth TWS earbuds.

- **Page 3: Quick AI Prompt**
  - Detects when supported AI apps are open on your desktop (`Gemini`, `ChatGPT`, `Codex`, `Claude`, `Claude Code`).
  - Lets you type a prompt directly inside the notch and press `Enter` to send it straight to the active AI app without manually switching windows.
  - Automatically hides the input box when no AI app is running.

- **Native Notification Interception**
  - Captures incoming notifications from **WhatsApp**, **LINE**, **Discord**, **Windows Security**, and other desktop apps, displaying them inside the Dynamic Island.
  - While Dynamic Island is running, default Windows toast banners in the bottom-right corner are automatically suppressed so you don't get duplicate popups. Closing Dynamic Island immediately restores standard Windows notifications.

- **System Tray & Startup Priority**
  - Right-click the System Tray icon (or the notch itself) and open **Settings** to enable **High Priority** and **Launch on Startup** so Dynamic Island starts automatically whenever your PC or laptop boots.

---

## Running the Release Build

1. Download and extract **`DynamicIsland-Release.zip`**.
2. Run **`Launcher.exe`**.
   - `Launcher.exe` uses a Microsoft-signed host binary (`Launcher.proj` bootstrapper) that automatically clears Windows *Mark-of-the-Web* (`Zone.Identifier`) metadata on extracted files and starts the app through the signed .NET runtime, avoiding false-positive blocks from Windows 11 Smart App Control.
3. To configure auto-start on boot or high process priority, right-click the tray icon and select **Settings**.
4. To close the app, right-click the tray icon (or the notch) and click **Exit**.

### Requirements

- Windows 10 (build 19041+) or Windows 11 (x64)
- [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or newer)

---

## Building from Source

```powershell
# Build Release binaries
dotnet build -c Release

# Launch via the signed bootstrapper
.\Launcher.exe
```

## Project Structure

```text
├── App.xaml / App.xaml.cs          # App entry point & Explicit AppUserModelID
├── MainWindow.xaml / .cs           # Spring-physics morphing UI, 3-page navigation, tray menu
├── SettingsWindow.xaml / .cs       # Settings popup (High Priority & Windows Startup)
├── Launcher.exe / Launcher.proj    # Smart App Control-safe signed launcher
├── Media/
│   ├── MediaManager.cs             # Windows GSMTC media session & timeline controller
│   ├── MediaModel.cs               # Track metadata & playback state model
│   └── LyricsService.cs            # LRCLIB timestamped LRC & plain lyrics fetcher
└── Native/
    ├── Win32.cs                    # Win32 P/Invoke window styles & kernel power APIs
    ├── PowerMonitor.cs             # Real-time charger & battery state monitor
    ├── AudioDeviceMonitor.cs       # Bluetooth TWS connection watcher
    ├── NotificationMonitor.cs      # Multi-layer toast reader & default banner suppressor
    ├── AiAppMonitor.cs             # Active AI app detector & prompt dispatcher
    └── AppSettings.cs              # Process priority & Registry startup persistence
```
