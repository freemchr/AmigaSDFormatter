using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace AmigaSdCreator.Windows;

/// <summary>Raises <see cref="Changed"/> when Windows reports a device arrival or removal (debounced).</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceChangeWatcher : IDisposable
{
    private readonly ManagementEventWatcher? _watcher;
    private readonly Timer _debounce;

    public event EventHandler? Changed;

    public DeviceChangeWatcher()
    {
        _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        try
        {
            // EventType 2 = arrival, 3 = removal.
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _watcher.EventArrived += (_, _) => _debounce.Change(750, Timeout.Infinite);
            _watcher.Start();
        }
        catch (ManagementException ex)
        {
            Debug.WriteLine($"Device change notifications unavailable: {ex.Message}");
            _watcher = null;
        }
    }

    public bool IsActive => _watcher is not null;

    public void Dispose()
    {
        try { _watcher?.Stop(); } catch (ManagementException) { }
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
