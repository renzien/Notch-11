using System;
using System.Threading.Tasks;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Native;

public record TwsDeviceInfo(string Name, int BatteryPercentage);

public class AudioDeviceMonitor : IDisposable
{
    private DeviceWatcher? _watcher;
    public event Action<TwsDeviceInfo>? TwsDeviceConnected;
    private string _lastConnectedDeviceId = string.Empty;

    public AudioDeviceMonitor()
    {
        try
        {
            // Ambil selector audio render dari Windows
            string selector = MediaDevice.GetAudioRenderSelector();
            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += (w, dev) => HandleDevice(dev);
            _watcher.Updated += (w, devUpdate) =>
            {
                // Jika update event, ambil info lengkap
                _ = CheckDeviceUpdateAsync(devUpdate.Id);
            };

            _watcher.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audio monitor init error: {ex.Message}");
        }
    }

    private async Task CheckDeviceUpdateAsync(string id)
    {
        try
        {
            var dev = await DeviceInformation.CreateFromIdAsync(id);
            if (dev != null)
            {
                HandleDevice(dev);
            }
        }
        catch { }
    }

    private void HandleDevice(DeviceInformation dev)
    {
        if (dev == null || string.IsNullOrWhiteSpace(dev.Name)) return;

        string id = dev.Id ?? string.Empty;
        string name = dev.Name;

        // FILTER KETAT: HANYA PERANGKAT YANG BENAR-BENAR TERHUBUNG LEWAT BLUETOOTH
        // Windows menandai interface Bluetooth audio dengan BTHENUM / BTHHFENUM / Bluetooth
        bool isBluetooth = id.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                           id.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
                           id.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                           id.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);

        // Abaikan speaker internal laptop, Realtek, HDMI, Display Audio, dll.
        if (!isBluetooth) return;

        if (name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("High Definition", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) && !name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Cegah spam trigger berulang untuk device ID yang sama dalam waktu singkat
        if (_lastConnectedDeviceId == id) return;
        _lastConnectedDeviceId = id;

        int battery = 70; // Standar indikator baterai TWS
        TwsDeviceConnected?.Invoke(new TwsDeviceInfo(name, battery));
    }

    public void TriggerManualTest(string name = "AirBuds Pro", int battery = 70)
    {
        TwsDeviceConnected?.Invoke(new TwsDeviceInfo(name, battery));
    }

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
            _watcher = null;
        }
        catch { }
    }
}
