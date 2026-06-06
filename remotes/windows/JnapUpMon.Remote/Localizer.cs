using System;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Net.ViaTheFalcon.JnapUpMon.Remote;

/// <summary>
/// Thin wrapper over the Windows App SDK resource loader, providing localised
/// strings from the project's .resw resources.
/// </summary>
internal static class Localizer
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>Returns the localised string for <paramref name="key"/>.</summary>
    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    /// <summary>Returns a localised, formatted string for <paramref name="key"/>.</summary>
    public static string Format(string key, params object[] args)
        => string.Format(Get(key), args);
}
