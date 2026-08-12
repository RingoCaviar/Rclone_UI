using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            using var chain = new X509Chain();
            if (!certificate.Subject.Contains("NAVIMATICS", StringComparison.OrdinalIgnoreCase) || !chain.Build((X509Certificate2)certificate)) return new("winfsp-signature-invalid");
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
