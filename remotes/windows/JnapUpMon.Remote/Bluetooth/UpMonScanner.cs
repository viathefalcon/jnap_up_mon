using System;
using Windows.Devices.Bluetooth.Advertisement;

namespace Net.ViaTheFalcon.JnapUpMon.Remote.Bluetooth;

/// <summary>
/// Carries the details of a single advertisement matching the UpMon service.
/// </summary>
public sealed class UpMonAdvertisementEventArgs : EventArgs
{
    public UpMonAdvertisementEventArgs(ulong bluetoothAddress, string? localName)
    {
        BluetoothAddress = bluetoothAddress;
        LocalName = localName;
    }

    public ulong BluetoothAddress { get; }

    public string? LocalName { get; }
}

/// <summary>
/// Continuously watches for BLE advertisements that expose the JNAP UpMon
/// service, raising <see cref="Discovered"/> for each matching advertisement.
/// The watcher keeps running in the background until <see cref="Stop"/> is called.
/// </summary>
public sealed class UpMonScanner
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;

    public UpMonScanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            // Active scanning solicits scan responses, which is where the
            // local name typically arrives.
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        // Only surface advertisements that include our 128-bit service UUID.
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(UpMonGatt.ServiceUuid);

        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    /// <summary>Raised on a background thread for each matching advertisement.</summary>
    public event EventHandler<UpMonAdvertisementEventArgs>? Discovered;

    /// <summary>Raised when the underlying watcher stops (e.g. the radio is turned off).</summary>
    public event EventHandler? Stopped;

    public bool IsRunning =>
        _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>
    /// Attempts to start background scanning. Returns <c>false</c> (rather than
    /// throwing) when the Bluetooth radio is off or otherwise unavailable, so the
    /// caller can surface a friendly message and retry later.
    /// </summary>
    public bool Start()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            return true;
        }

        try
        {
            _watcher.Start();
            return true;
        }
        catch (Exception)
        {
            // Typically ERROR_DEVICE_NOT_AVAILABLE (0x800710DF) when Bluetooth is
            // turned off or there is no BLE adapter present.
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Stop();
            }
        }
        catch (Exception)
        {
            // The radio may have already gone away; nothing to do.
        }
    }

    private void OnReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var localName = args.Advertisement?.LocalName;
        Discovered?.Invoke(
            this,
            new UpMonAdvertisementEventArgs(
                args.BluetoothAddress,
                string.IsNullOrWhiteSpace(localName) ? null : localName));
    }

    private void OnStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        => Stopped?.Invoke(this, EventArgs.Empty);
}
