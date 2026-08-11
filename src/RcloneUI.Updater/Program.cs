namespace RcloneUI.Updater;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        byte[] token;
        try { token = Convert.FromHexString(args[2]); }
        catch (FormatException) { return 2; }
        try { return PlanDrivenUpdater.ExecuteAsync(args[0], args[1], token).GetAwaiter().GetResult(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(token); }
    }
}
