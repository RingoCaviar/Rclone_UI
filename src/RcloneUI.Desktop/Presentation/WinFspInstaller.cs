using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RcloneUI.Desktop.Presentation;

public sealed record WinFspInstallResult(string ResultType, string? Detail = null);

public interface IWinFspInstaller
{
    ValueTask<WinFspInstallResult> InstallAsync(CancellationToken cancellationToken = default);
}

public sealed class OfficialStableWinFspInstaller(string dataRootPath, HttpClient? httpClient = null) : IWinFspInstaller
{
    public const string Version = "2.1.25156";
    public const string Sha256 = "073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A";
    public static readonly Uri DownloadUri = new("https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi");
    private readonly HttpClient http = httpClient ?? new HttpClient();

    public async ValueTask<WinFspInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(dataRootPath, "runtime", "winfsp-installer");
        var path = Path.Combine(directory, $"winfsp-{Version}.msi");
        Directory.CreateDirectory(directory);
        try
        {
            using var response = await http.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new("winfsp-download-failed", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            using var stream = File.OpenRead(path);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sha256), SHA256.HashData(stream))) return new("winfsp-hash-mismatch");
            if (!WindowsAuthenticodeVerifier.IsTrusted(path)) return new("winfsp-signature-invalid");
            using var installer = Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{path}\" /passive /norestart",
                UseShellExecute = true,
                Verb = "runas"
            });
            if (installer is null) return new("winfsp-installer-not-started");
            await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return installer.ExitCode is 0 or 3010 ? new("winfsp-install-complete", installer.ExitCode == 3010 ? "restart-required" : null) : new("winfsp-installer-failed", installer.ExitCode.ToString(CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) { return new("winfsp-install-cancelled"); }
        catch (System.ComponentModel.Win32Exception) { return new("winfsp-uac-cancelled"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException) { return new("winfsp-install-failed", exception.GetType().Name); }
    }
}

public static class WindowsAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var trustPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var trust = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0,
                dwUIContext = 0,
            };
            trustPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trust, trustPointer, false);
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, trustPointer) == 0;
        }
        finally
        {
            if (trustPointer != IntPtr.Zero) Marshal.FreeHGlobal(trustPointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
