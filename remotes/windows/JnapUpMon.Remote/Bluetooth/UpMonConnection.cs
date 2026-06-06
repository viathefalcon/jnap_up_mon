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
    private readonly GattCharacteristic _run;
    private readonly GattCharacteristic _reboot;
    private readonly GattCharacteristic _reset;

    private UpMonConnection(
        BluetoothLEDevice device,
        GattDeviceService service,
        GattCharacteristic mrr,
        GattCharacteristic run,
        GattCharacteristic reboot,
        GattCharacteristic reset)
    {
        _device = device;
        _service = service;
        _mrr = mrr;
        _run = run;
        _reboot = reboot;
        _reset = reset;
    }

    /// <summary>Raised when the underlying device connection is lost.</summary>
    public event EventHandler? ConnectionLost;

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
            GattCharacteristic? run = await GetCharacteristicAsync(service, UpMonGatt.RunCharacteristicUuid);
            GattCharacteristic? reboot = await GetCharacteristicAsync(service, UpMonGatt.RebootCharacteristicUuid);
            GattCharacteristic? reset = await GetCharacteristicAsync(service, UpMonGatt.ResetCharacteristicUuid);

            if (mrr is null || run is null || reboot is null || reset is null)
            {
                service.Dispose();
                device.Dispose();
                return null;
            }

            var connection = new UpMonConnection(device, service, mrr, run, reboot, reset);
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
            if (read.Status != GattCommunicationStatus.Success || read.Value is null)
            {
                return null;
            }

            using DataReader reader = DataReader.FromBuffer(read.Value);
            if (reader.UnconsumedBufferLength < sizeof(uint))
            {
                return null;
            }

            // ArduinoBLE transmits multi-byte values little-endian.
            reader.ByteOrder = ByteOrder.LittleEndian;
            return reader.ReadUInt32();
        }
        catch
        {
            return null;
        }
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
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service.Dispose();
        _device.Dispose();
    }
}
