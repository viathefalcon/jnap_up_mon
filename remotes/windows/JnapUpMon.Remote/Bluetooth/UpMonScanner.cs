using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

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
/// The outcome of trying to start BLE advertisement scanning.
/// </summary>
public enum UpMonScanStartResult
{
    Started,
    AlreadyRunning,
    NoBluetoothAdapter,
    BluetoothTurnedOff,
    BluetoothDisabled,
    BluetoothUnavailable,
    Failed,
}

/// <summary>
/// Why the scanner stopped after previously running.
/// </summary>
public enum UpMonScanStoppedReason
{
    Unknown,
    NoBluetoothAdapter,
    BluetoothTurnedOff,
    BluetoothDisabled,
    BluetoothUnavailable,
}

/// <summary>
/// Carries diagnostics for why scanning stopped.
/// </summary>
public sealed class UpMonScannerStoppedEventArgs : EventArgs
{
    public UpMonScannerStoppedEventArgs(
        UpMonScanStoppedReason reason,
        BluetoothError bluetoothError)
    {
        Reason = reason;
        BluetoothError = bluetoothError;
    }

    public UpMonScanStoppedReason Reason { get; }

    public BluetoothError BluetoothError { get; }
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

    /// <summary>
    /// Raised when the underlying watcher stops, including diagnostics about why.
    /// </summary>
    public event EventHandler<UpMonScannerStoppedEventArgs>? Stopped;

    public bool IsRunning =>
        _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>
    /// Attempts to start background scanning and returns a detailed outcome that
    /// distinguishes adapter absence from radio-off and other unavailable states.
    /// </summary>
    public async Task<UpMonScanStartResult> StartAsync()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            return UpMonScanStartResult.AlreadyRunning;
        }

        UpMonScanStartResult availability = await GetAvailabilityAsync();
        if (availability != UpMonScanStartResult.Started)
        {
            return availability;
        }

        try
        {
            _watcher.Start();
            return UpMonScanStartResult.Started;
        }
        catch (Exception)
        {
            // Re-check availability in case the radio changed between probing and start.
            UpMonScanStartResult current = await GetAvailabilityAsync();
            return current == UpMonScanStartResult.Started
                ? UpMonScanStartResult.Failed
                : current;
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
        => _ = RaiseStoppedAsync(args.Error);

    private async Task RaiseStoppedAsync(BluetoothError error)
    {
        UpMonScanStoppedReason reason = await GetStoppedReasonAsync(error);
        Stopped?.Invoke(this, new UpMonScannerStoppedEventArgs(reason, error));
    }

    private static async Task<UpMonScanStartResult> GetAvailabilityAsync()
    {
        try
        {
            BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                return UpMonScanStartResult.NoBluetoothAdapter;
            }

            var radios = await Radio.GetRadiosAsync();
            Radio? bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bluetoothRadio is null)
            {
                return UpMonScanStartResult.NoBluetoothAdapter;
            }

            return bluetoothRadio.State switch
            {
                RadioState.On => UpMonScanStartResult.Started,
                RadioState.Off => UpMonScanStartResult.BluetoothTurnedOff,
                RadioState.Disabled => UpMonScanStartResult.BluetoothDisabled,
                _ => UpMonScanStartResult.BluetoothUnavailable,
            };
        }
        catch
        {
            return UpMonScanStartResult.BluetoothUnavailable;
        }
    }

    private static async Task<UpMonScanStoppedReason> GetStoppedReasonAsync(BluetoothError error)
    {
        UpMonScanStartResult availability = await GetAvailabilityAsync();
        if (availability == UpMonScanStartResult.NoBluetoothAdapter)
        {
            return UpMonScanStoppedReason.NoBluetoothAdapter;
        }

        if (availability == UpMonScanStartResult.BluetoothTurnedOff)
        {
            return UpMonScanStoppedReason.BluetoothTurnedOff;
        }

        if (availability == UpMonScanStartResult.BluetoothDisabled)
        {
            return UpMonScanStoppedReason.BluetoothDisabled;
        }

        if (availability == UpMonScanStartResult.BluetoothUnavailable)
        {
            return UpMonScanStoppedReason.BluetoothUnavailable;
        }

        return error == BluetoothError.RadioNotAvailable
            ? UpMonScanStoppedReason.BluetoothUnavailable
            : UpMonScanStoppedReason.Unknown;
    }
}
