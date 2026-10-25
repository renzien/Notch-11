using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Native;

public class AppSettingsData
{
    public bool HighPriority { get; set; } = false;
    public bool RunOnStartup { get; set; } = false;
}

public static class AppSettings
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "DynamicIsland";

    public static AppSettingsData Current { get; private set; } = new();

    private static string GetSettingsFilePath()
    {
        try
        {
            string localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIsland");
            Directory.CreateDirectory(localDir);
            return Path.Combine(localDir, "settings.json");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        }
    }

    public static void LoadAndApply()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (loaded != null)
                {
                    Current = loaded;
                }
            }
            else
            {
                // Sinkronkan dengan status Registry saat ini jika file belum ada
                Current.RunOnStartup = IsStartupRegistered();
            }
        }
        catch
        {
            Current = new AppSettingsData();
        }

        ApplyProcessPriority(Current.HighPriority);
        if (Current.RunOnStartup)
        {
            ApplyRunOnStartup(true);
        }
    }

    public static void Save(bool highPriority, bool runOnStartup)
    {
        Current.HighPriority = highPriority;
        Current.RunOnStartup = runOnStartup;

        ApplyProcessPriority(highPriority);
        ApplyRunOnStartup(runOnStartup);

        try
        {
            string path = GetSettingsFilePath();
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save settings error: {ex.Message}");
        }
    }

    public static void ApplyProcessPriority(bool highPriority)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = highPriority ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyProcessPriority error: {ex.Message}");
        }
    }

    public static bool IsStartupRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            return key?.GetValue(AppRegistryName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyRunOnStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
            if (key == null) return;

            if (enable)
            {
                string command = GetBestStartupCommand();
                key.SetValue(AppRegistryName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyRunOnStartup error: {ex.Message}");
        }
    }

    private static string GetBestStartupCommand()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 1. Cek Launcher.exe di folder saat ini atau parent folder (versi Release / Root)
        string[] candidateDirs =
        {
            baseDir,
            Path.GetFullPath(Path.Combine(baseDir, "..")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."))
        };

        foreach (string dir in candidateDirs)
        {
            string launcherPath = Path.Combine(dir, "Launcher.exe");
            if (File.Exists(launcherPath))
            {
                return $"\"{launcherPath}\"";
            }
        }

        foreach (string dir in candidateDirs)
        {
            string vbsPath = Path.Combine(dir, "Jalankan-DynamicIsland.vbs");
            if (File.Exists(vbsPath))
            {
                return $"wscript.exe \"{vbsPath}\"";
            }
        }

        string dllPath = Path.Combine(baseDir, "DynamicIsland.dll");
        if (File.Exists(dllPath))
        {
            return $"dotnet \"{dllPath}\"";
        }

        string exePath = Path.Combine(baseDir, "DynamicIsland.exe");
        return $"\"{exePath}\"";
    }
}
