using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Net.ViaTheFalcon.JnapUpMon.Remote.Bluetooth;

/// <summary>
/// The action that a writable characteristic performs when triggered.
/// </summary>
public enum UpMonAction
{
    Run,
    Reboot,
    Reset,
}

/// <summary>
/// Wraps a GATT connection to a single UpMon service instance and exposes
/// strongly-typed access to its characteristics.
/// </summary>
public sealed class UpMonConnection : IDisposable
{
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _mrr;
    private readonly GattCharacteristic _mru;
    private readonly GattCharacteristic _run;
    private readonly GattCharacteristic _reboot;
    private readonly GattCharacteristic _reset;

    private bool _mrrNotificationsEnabled;
    private bool _mruNotificationsEnabled;

    private UpMonConnection(
        BluetoothLEDevice device,
        GattDeviceService service,
        GattCharacteristic mrr,
        GattCharacteristic mru,
        GattCharacteristic run,
        GattCharacteristic reboot,
        GattCharacteristic reset)
    {
        _device = device;
        _service = service;
        _mrr = mrr;
        _mru = mru;
        _run = run;
        _reboot = reboot;
        _reset = reset;
    }

    /// <summary>Raised when the underlying device connection is lost.</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>
    /// Raised when the device pushes a new "most recent reboot" value via notification.
    /// The argument is the milliseconds elapsed since the most recent reboot request, or
    /// <c>null</c> when the value could not be decoded.
    /// </summary>
    public event EventHandler<uint?>? MostRecentRebootChanged;

    /// <summary>
    /// Raised when the device pushes a new "most recent run" value via notification.
    /// The argument is the milliseconds elapsed since the most recent run, or
    /// <c>null</c> when the value could not be decoded.
    /// </summary>
    public event EventHandler<uint?>? MostRecentRunChanged;

    /// <summary>
    /// The connected device's name as reported by the OS, or <c>null</c> when it
    /// is unknown.
    /// </summary>
    public string? DeviceName =>
        string.IsNullOrWhiteSpace(_device.Name) ? null : _device.Name;

    /// <summary>
    /// Connects to the device at <paramref name="bluetoothAddress"/>, discovers the
    /// UpMon service and resolves every characteristic. Returns <c>null</c> if the
    /// device or any required characteristic could not be found.
    /// </summary>
    public static async Task<UpMonConnection?> ConnectAsync(ulong bluetoothAddress)
    {
        BluetoothLEDevice? device =
            await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
        if (device is null)
        {
            return null;
        }

        try
        {
            GattDeviceServicesResult servicesResult =
                await device.GetGattServicesForUuidAsync(
                    UpMonGatt.ServiceUuid,
                    BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success ||
                servicesResult.Services.Count == 0)
            {
                device.Dispose();
                return null;
            }

            GattDeviceService service = servicesResult.Services[0];

            GattCharacteristic? mrr = await GetCharacteristicAsync(service, UpMonGatt.MrrCharacteristicUuid);
            GattCharacteristic? mru = await GetCharacteristicAsync(service, UpMonGatt.MruCharacteristicUuid);
            GattCharacteristic? run = await GetCharacteristicAsync(service, UpMonGatt.RunCharacteristicUuid);
            GattCharacteristic? reboot = await GetCharacteristicAsync(service, UpMonGatt.RebootCharacteristicUuid);
            GattCharacteristic? reset = await GetCharacteristicAsync(service, UpMonGatt.ResetCharacteristicUuid);

            if (mrr is null || mru is null || run is null || reboot is null || reset is null)
            {
                service.Dispose();
                device.Dispose();
                return null;
            }

            var connection = new UpMonConnection(device, service, mrr, mru, run, reboot, reset);
            device.ConnectionStatusChanged += connection.OnConnectionStatusChanged;
            return connection;
        }
        catch
        {
            device.Dispose();
            return null;
        }
    }

    private static async Task<GattCharacteristic?> GetCharacteristicAsync(
        GattDeviceService service,
        Guid characteristicUuid)
    {
        GattCharacteristicsResult result =
            await service.GetCharacteristicsForUuidAsync(
                characteristicUuid,
                BluetoothCacheMode.Uncached);
        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
            ? result.Characteristics[0]
            : null;
    }

    /// <summary>
    /// Reads the "most recent reboot" characteristic and returns the number of
    /// milliseconds since the last reboot request, or <c>null</c> on failure.
    /// </summary>
    public async Task<uint?> ReadMostRecentRebootMillisAsync()
    {
        try
        {
            GattReadResult read = await _mrr.ReadValueAsync(BluetoothCacheMode.Uncached);
            return read.Status == GattCommunicationStatus.Success
                ? TryDecodeMillis(read.Value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the "most recent run" characteristic and returns the number of
    /// milliseconds since the last run, or <c>null</c> on failure.
    /// </summary>
    public async Task<uint?> ReadMostRecentRunMillisAsync()
    {
        try
        {
            GattReadResult read = await _mru.ReadValueAsync(BluetoothCacheMode.Uncached);
            return read.Status == GattCommunicationStatus.Success
                ? TryDecodeMillis(read.Value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Subscribes to notifications from the "most recent reboot" characteristic so that
    /// <see cref="MostRecentRebootChanged"/> fires whenever the device pushes a new
    /// value. Returns <c>true</c> when the subscription was established.
    /// </summary>
    public async Task<bool> StartMostRecentRebootNotificationsAsync()
    {
        try
        {
            GattCommunicationStatus status =
                await _mrr.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                return false;
            }

            _mrr.ValueChanged += OnMrrValueChanged;
            _mrrNotificationsEnabled = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribes to notifications from the "most recent run" characteristic so that
    /// <see cref="MostRecentRunChanged"/> fires whenever the device pushes a new
    /// value. Returns <c>true</c> when the subscription was established.
    /// </summary>
    public async Task<bool> StartMostRecentRunNotificationsAsync()
    {
        try
        {
            GattCommunicationStatus status =
                await _mru.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                return false;
            }

            _mru.ValueChanged += OnMruValueChanged;
            _mruNotificationsEnabled = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnMruValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        => MostRecentRunChanged?.Invoke(this, TryDecodeMillis(args.CharacteristicValue));

    private void OnMrrValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        => MostRecentRebootChanged?.Invoke(this, TryDecodeMillis(args.CharacteristicValue));

    /// <summary>
    /// Decodes a little-endian unsigned 32-bit value from a GATT buffer, returning
    /// <c>null</c> when the buffer is missing or too short.
    /// </summary>
    private static uint? TryDecodeMillis(IBuffer? buffer)
    {
        if (buffer is null)
        {
            return null;
        }

        using DataReader reader = DataReader.FromBuffer(buffer);
        if (reader.UnconsumedBufferLength < sizeof(uint))
        {
            return null;
        }

        // ArduinoBLE transmits multi-byte values little-endian.
        reader.ByteOrder = ByteOrder.LittleEndian;
        return reader.ReadUInt32();
    }

    /// <summary>
    /// Writes the trigger value (1) to the characteristic for the requested action.
    /// </summary>
    public async Task<bool> TriggerAsync(UpMonAction action)
    {
        GattCharacteristic characteristic = action switch
        {
            UpMonAction.Run => _run,
            UpMonAction.Reboot => _reboot,
            UpMonAction.Reset => _reset,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        try
        {
            using var writer = new DataWriter();
            writer.WriteByte(1);

            GattCommunicationStatus status = await characteristic.WriteValueAsync(
                writer.DetachBuffer(),
                GattWriteOption.WriteWithResponse);
            return status == GattCommunicationStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_mrrNotificationsEnabled)
        {
            _mrr.ValueChanged -= OnMrrValueChanged;
            _mrrNotificationsEnabled = false;
        }

        if (_mruNotificationsEnabled)
        {
            _mru.ValueChanged -= OnMruValueChanged;
            _mruNotificationsEnabled = false;
        }

        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service.Dispose();
        _device.Dispose();
    }
}
