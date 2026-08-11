using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RcloneUI.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(AppContext.BaseDirectory);
            var desktop = Path.GetFullPath(Path.Combine(root, "desktop", "RcloneUI.exe"));
            if (!desktop.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(desktop))
                return Fail("The portable package is incomplete. Extract the full ZIP before starting Rclone UI.");

            var start = new ProcessStartInfo { FileName = desktop, UseShellExecute = false };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            return Fail($"Rclone UI could not start.\n\n{exception.Message}");
        }
    }

    private static int Fail(string message)
    {
        if (MessageBox(nint.Zero, message, "Rclone UI", 0x10) == 0)
            Debug.WriteLine("The portable launcher error dialog could not be displayed.");
        return 1;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
