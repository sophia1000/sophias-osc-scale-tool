using System.Runtime.InteropServices;

namespace VrcHeightOsc.App;

internal static class SingleInstanceWindow
{
    private const int Restore = 9;

    public static void ActivateExisting()
    {
        var window = FindWindow(null, AppConstants.Name);
        if (window == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(window, Restore);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
