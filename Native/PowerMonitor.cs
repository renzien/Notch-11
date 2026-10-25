using System;
using Microsoft.Win32;

namespace DynamicIsland.Native;

public record PowerInfo(bool IsPluggedIn, bool IsCharging, int BatteryPercentage, bool HasBattery);

public class PowerMonitor : IDisposable
{
    private bool _lastPluggedIn;
    private int _lastPercent;
    private readonly object _lock = new();

    public event Action<PowerInfo>? ChargerPluggedIn;
    public event Action<PowerInfo>? PowerStateChanged;

    public PowerMonitor()
    {
        var current = GetCurrentPowerInfo();
        _lastPluggedIn = current.IsPluggedIn;
        _lastPercent = current.BatteryPercentage;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            CheckPowerChange();
        }
    }

    public void HandlePowerBroadcast()
    {
        CheckPowerChange();
    }

    public PowerInfo Refresh()
    {
        CheckPowerChange();
        return GetCurrentPowerInfo();
    }

    private void CheckPowerChange()
    {
        lock (_lock)
        {
            var info = GetCurrentPowerInfo();
            bool plugChanged = (info.IsPluggedIn != _lastPluggedIn);
            bool percentChanged = (info.BatteryPercentage != _lastPercent);

            if (plugChanged)
            {
                _lastPluggedIn = info.IsPluggedIn;
                if (info.IsPluggedIn)
                {
                    ChargerPluggedIn?.Invoke(info);
                }
            }

            if (percentChanged || plugChanged)
            {
                _lastPercent = info.BatteryPercentage;
                PowerStateChanged?.Invoke(info);
            }
        }
    }

    public static PowerInfo GetCurrentPowerInfo()
    {
        if (Win32.GetSystemPowerStatus(out var status))
        {
            bool hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent != 255;
            bool isPluggedIn = status.ACLineStatus == 1;
            bool isCharging = (status.BatteryFlag & 8) != 0 || (isPluggedIn && status.BatteryLifePercent < 100);
            int percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;

            return new PowerInfo(isPluggedIn, isCharging, percent, hasBattery);
        }

        return new PowerInfo(false, false, 100, false);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
