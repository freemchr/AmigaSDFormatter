using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AmigaSdCreator.Windows;

[SupportedOSPlatform("windows")]
public static class Elevation
{
    /// <summary>
    /// Starts a new elevated instance of the current executable. Returns false if the user declined
    /// the UAC prompt. The caller should exit after a successful restart.
    /// </summary>
    public static bool TryRestartElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application path.");
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
