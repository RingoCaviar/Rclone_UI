using System.Security.Cryptography;
using System.Text.Json;

namespace RcloneUI.Updater;

public sealed record UpdaterFile(string RelativePath, string ActiveRelativePath, string StagedSha256);
public sealed record UpdaterPlan(Guid TransactionId, int ParentProcessId, string TokenSha256, string StagingDirectory, IReadOnlyList<UpdaterFile> Files);

public static class PlanDrivenUpdater
{
    public static async Task<int> ExecuteAsync(string trustedUpdatesRoot, string planPath, ReadOnlyMemory<byte> token, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(trustedUpdatesRoot);
        var applicationRoot = Directory.GetParent(root)?.FullName ?? throw new InvalidDataException("Updates root has no application parent.");
        var absolutePlan = RequireChild(root, planPath);
        var plan = JsonSerializer.Deserialize<UpdaterPlan>(await File.ReadAllTextAsync(absolutePlan, cancellationToken).ConfigureAwait(false));
        if (plan is null || plan.TransactionId == Guid.Empty || !FixedEquals(plan.TokenSha256, Convert.ToHexString(SHA256.HashData(token.Span)))) return 20;
        var staging = RequireChild(root, plan.StagingDirectory);
        foreach (var file in plan.Files)
        {
            var staged = RequireChild(staging, file.RelativePath);
            if (!File.Exists(staged) || !FixedEquals(file.StagedSha256, await HashFileAsync(staged, cancellationToken).ConfigureAwait(false))) return 21;
        }
        if (plan.ParentProcessId > 0)
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(plan.ParentProcessId); await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (ArgumentException) { }
        }
        var rollback = Directory.CreateDirectory(Path.Combine(root, "rollback", plan.TransactionId.ToString("N"))).FullName;
        var replaced = new Stack<(string Active, string Backup)>();
        try
        {
            foreach (var file in plan.Files)
            {
                var staged = RequireChild(staging, file.RelativePath);
                var active = RequireChild(applicationRoot, file.ActiveRelativePath);
                var backup = RequireChild(rollback, file.ActiveRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(active)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (File.Exists(active)) File.Replace(staged, active, backup, ignoreMetadataErrors: false);
                else { File.Move(staged, active); File.WriteAllText(backup + ".absent", string.Empty); }
                replaced.Push((active, backup));
            }
            return 0;
        }
        catch
        {
            while (replaced.TryPop(out var item))
            {
                if (File.Exists(item.Backup)) File.Replace(item.Backup, item.Active, null, ignoreMetadataErrors: false);
                else if (File.Exists(item.Backup + ".absent")) File.Delete(item.Active);
            }
            return 22;
        }
    }

    private static string RequireChild(string root, string value)
    {
        var path = Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Updater plan escaped its trusted root.");
        return path;
    }
    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
    private static bool FixedEquals(string left, string right) { try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); } catch (FormatException) { return false; } }
}
