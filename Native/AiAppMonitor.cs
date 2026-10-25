using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace DynamicIsland.Native;

public enum AiBrand
{
    Gemini,
    ChatGpt,
    Codex,
    Claude,
    ClaudeCode
}

public record DetectedAiApp(
    AiBrand Brand,
    string DisplayName,
    string WindowTitle,
    IntPtr WindowHandle,
    string AccentHex
);

public static class AiAppMonitor
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static string GetWindowTextString(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString().Trim() : string.Empty;
    }

    /// <summary>
    /// Mendeteksi semua aplikasi AI (Gemini, ChatGPT, Codex, Claude, Claude Code) yang sedang terbuka.
    /// </summary>
    public static List<DetectedAiApp> GetOpenAiApps(IntPtr ownHwnd)
    {
        var list = new List<DetectedAiApp>();
        uint currentPid = (uint)Environment.ProcessId;

        // Cek apakah ada proses CLI claude / codex yang sedang berjalan di terminal
        bool hasClaudeCliProc = false;
        bool hasCodexCliProc = false;
        try
        {
            hasClaudeCliProc = Process.GetProcessesByName("claude").Length > 0;
            hasCodexCliProc = Process.GetProcessesByName("codex").Length > 0;
        }
        catch { }

        EnumWindows((hWnd, lParam) =>
        {
            if (hWnd == ownHwnd || !IsWindowVisible(hWnd)) return true;

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentPid) return true;

            string title = GetWindowTextString(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.Equals("Dynamic Island", StringComparison.OrdinalIgnoreCase)) return true;

            string procName = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName ?? string.Empty;
            }
            catch { }

            // 1. Cek Claude Code (Terminal / IDE / Window Title)
            if (title.Contains("Claude Code", StringComparison.OrdinalIgnoreCase) ||
                (IsTerminalProcess(procName) && (title.Contains("claude", StringComparison.OrdinalIgnoreCase) || hasClaudeCliProc)))
            {
                list.Add(new DetectedAiApp(AiBrand.ClaudeCode, "Claude Code", title, hWnd, "#D97757"));
                return true;
            }

            // 2. Cek Claude Desktop atau Claude Web
            if (procName.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Claude", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new DetectedAiApp(AiBrand.Claude, "Claude", title, hWnd, "#D97757"));
                return true;
            }

            // 3. Cek OpenAI Codex (App / CLI / Web)
            if (procName.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
                (IsTerminalProcess(procName) && hasCodexCliProc))
            {
                list.Add(new DetectedAiApp(AiBrand.Codex, "Codex", title, hWnd, "#10A37F"));
                return true;
            }

            // 4. Cek ChatGPT Desktop atau ChatGPT Web
            if (procName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new DetectedAiApp(AiBrand.ChatGpt, "ChatGPT", title, hWnd, "#10A37F"));
                return true;
            }

            // 5. Cek Google Gemini App / PWA / Web
            if (procName.Equals("Gemini", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new DetectedAiApp(AiBrand.Gemini, "Gemini", title, hWnd, "#4E82EE"));
                return true;
            }

            return true;
        }, IntPtr.Zero);

        // Hilangkan duplikat per brand + window handle
        return list
            .GroupBy(a => a.WindowHandle)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsTerminalProcess(string procName)
    {
        return procName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("ConEmu64", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("wezterm-gui", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("alacritty", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mengirimkan teks prompt langsung ke jendela aplikasi AI yang dituju (Focus -> Paste -> Enter).
    /// </summary>
    public static async Task<bool> SendPromptToAiAppAsync(DetectedAiApp target, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || target.WindowHandle == IntPtr.Zero) return false;

        try
        {
            // 1. Salin prompt ke Clipboard pada UI Thread
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(prompt);
            });

            // 2. Restore window jika sedang di-minimize, lalu bawa ke Foreground
            await Task.Run(async () =>
            {
                IntPtr targetHwnd = target.WindowHandle;
                if (Win32.IsIconic(targetHwnd))
                {
                    Win32.ShowWindow(targetHwnd, Win32.SW_RESTORE);
                    await Task.Delay(140);
                }

                ForceForegroundWindow(targetHwnd);
                await Task.Delay(140);

                // 3. Coba fokuskan kolom input chat (Edit/Document) di dalam aplikasi AI bila bukan terminal
                TryFocusChatInputElement(targetHwnd);
                await Task.Delay(70);

                // 4. Tekan Ctrl+V untuk menempelkan prompt
                Win32.keybd_event(Win32.VK_CONTROL, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_V, 0, 0, UIntPtr.Zero);
                await Task.Delay(35);
                Win32.keybd_event(Win32.VK_V, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_CONTROL, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

                await Task.Delay(90);

                // 5. Tekan Enter untuk mengirim prompt
                Win32.keybd_event(Win32.VK_RETURN, 0, 0, UIntPtr.Zero);
                await Task.Delay(25);
                Win32.keybd_event(Win32.VK_RETURN, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            });

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SendPromptToAiAppAsync error: {ex.Message}");
            return false;
        }
    }

    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        try
        {
            IntPtr fgHwnd = Win32.GetForegroundWindow();
            uint fgThread = Win32.GetWindowThreadProcessId(fgHwnd, out _);
            uint curThread = Win32.GetCurrentThreadId();

            if (fgThread != curThread && fgThread != 0)
            {
                Win32.AttachThreadInput(curThread, fgThread, true);
                Win32.SetForegroundWindow(hWnd);
                Win32.AttachThreadInput(curThread, fgThread, false);
            }
            else
            {
                Win32.SetForegroundWindow(hWnd);
            }
        }
        catch
        {
            Win32.SetForegroundWindow(hWnd);
        }
    }

    private static void TryFocusChatInputElement(IntPtr targetHwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(targetHwnd);
            if (root == null) return;

            var editCond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
            );

            var inputs = root.FindAll(TreeScope.Descendants, editCond);
            if (inputs == null || inputs.Count == 0) return;

            // Cari elemen Edit/Document yang aktif dan berada paling bawah (posisi khas chat prompt box)
            AutomationElement? bestInput = null;
            double maxBottom = -1;

            foreach (AutomationElement el in inputs)
            {
                try
                {
                    if (!el.Current.IsEnabled || el.Current.IsOffscreen) continue;
                    var rect = el.Current.BoundingRectangle;
                    if (rect.Width < 80 || rect.Height < 16) continue;

                    if (rect.Bottom > maxBottom)
                    {
                        maxBottom = rect.Bottom;
                        bestInput = el;
                    }
                }
                catch { }
            }

            bestInput?.SetFocus();
        }
        catch { }
    }
}