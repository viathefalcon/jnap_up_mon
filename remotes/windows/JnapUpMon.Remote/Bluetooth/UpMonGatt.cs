using System;

namespace Net.ViaTheFalcon.JnapUpMon.Remote.Bluetooth;

/// <summary>
/// GATT identifiers exposed by the Arduino sketch (see sketch_jnap_upmon.ino).
/// </summary>
internal static class UpMonGatt
{
    /// <summary>The primary service advertised by the device.</summary>
    public static readonly Guid ServiceUuid =
        new("505F8A1F-3872-449E-9167-B3549A5D7A87");

    /// <summary>Read-only: milliseconds elapsed since the most recent reboot request.</summary>
    public static readonly Guid MrrCharacteristicUuid =
        new("43ADDD14-843B-407C-9B40-696E3819B4AE");

    /// <summary>Write: triggers the connect/read/reboot procedure immediately.</summary>
    public static readonly Guid RunCharacteristicUuid =
        new("E2C0FF71-A900-434D-9C39-6465443F3F5A");

    /// <summary>Write: triggers the reboot procedure immediately.</summary>
    public static readonly Guid RebootCharacteristicUuid =
        new("143E8851-01C0-49ED-8F37-9D287B6B32C7");

    /// <summary>Write: resets the "most recent reboot" timestamp and turns the LED on.</summary>
    public static readonly Guid ResetCharacteristicUuid =
        new("B6C3D7F2-28E7-4C95-B6AB-65D34D7D9E13");
}
