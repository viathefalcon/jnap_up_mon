using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Net.ViaTheFalcon.JnapUpMon.Remote.Bluetooth;

/// <summary>
/// Represents a single discovered instance of the JNAP UpMon BLE service.
/// Instances are identified (and de-duplicated) by their Bluetooth address.
/// </summary>
public sealed class UpMonInstance : INotifyPropertyChanged
{
    private string? _name;

    public UpMonInstance(ulong bluetoothAddress, string? name)
    {
        BluetoothAddress = bluetoothAddress;
        _name = name;
        LastSeen = DateTimeOffset.UtcNow;
    }

    /// <summary>The 48-bit Bluetooth device address; unique per physical device.</summary>
    public ulong BluetoothAddress { get; }

    /// <summary>The advertised local name, if any.</summary>
    public string? Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>Timestamp of the most recent advertisement; used to prune stale entries.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>The colon-separated hexadecimal representation of the address.</summary>
    public string Address => FormatAddress(BluetoothAddress);

    /// <summary>A user-facing label combining the name (when known) and the address.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_name) ? Address : $"{_name} ({Address})";

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        // The address occupies the lowest 6 bytes, most-significant first.
        return string.Join(
            ":",
            bytes[5].ToString("X2"),
            bytes[4].ToString("X2"),
            bytes[3].ToString("X2"),
            bytes[2].ToString("X2"),
            bytes[1].ToString("X2"),
            bytes[0].ToString("X2"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public override bool Equals(object? obj)
        => obj is UpMonInstance other && other.BluetoothAddress == BluetoothAddress;

    public override int GetHashCode() => BluetoothAddress.GetHashCode();
}
