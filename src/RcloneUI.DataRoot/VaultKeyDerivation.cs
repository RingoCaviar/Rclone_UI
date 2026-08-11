using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RcloneUI.DataRoot;

internal readonly record struct Argon2Parameters(int MemoryKiB, int Iterations, int Lanes)
{
    internal const int MinimumMemoryKiB = 65_536;
    internal const int MaximumMemoryKiB = 1_048_576;
    internal const int MinimumIterations = 3;
    internal const int MaximumIterations = 10;
    internal const int MinimumLanes = 4;
    internal const int MaximumLanes = 16;

    internal static Argon2Parameters Default => new(MinimumMemoryKiB, MinimumIterations, MinimumLanes);

    internal void Validate()
    {
        if (MemoryKiB is < MinimumMemoryKiB or > MaximumMemoryKiB
            || Iterations is < MinimumIterations or > MaximumIterations
            || Lanes is < MinimumLanes or > MaximumLanes)
        {
            throw new VaultFormatException("argon2-parameters-out-of-policy");
        }
    }
}

internal interface IVaultKeyDeriver
{
    void Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters, Span<byte> output);
}

internal sealed class LibArgon2KeyDeriver : IVaultKeyDeriver, IDisposable
{
    private static readonly SemaphoreSlim Admission = new(1, 1);
    private readonly nint library;
    private readonly Argon2IdHashRaw derive;

    internal LibArgon2KeyDeriver(LibArgon2Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var path = Path.GetFullPath(binding.AbsoluteLibraryPath);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The verified libargon2 binary is unavailable.", path);
        }

        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(digest),
                Convert.FromHexString(binding.Sha256Digest)))
        {
            throw new CryptographicException("The libargon2 binary digest does not match the pinned manifest.");
        }

        library = NativeLibrary.Load(path);
        derive = Marshal.GetDelegateForFunctionPointer<Argon2IdHashRaw>(NativeLibrary.GetExport(library, "argon2id_hash_raw"));
    }

    public unsafe void Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Argon2Parameters parameters, Span<byte> output)
    {
        parameters.Validate();
        if (salt.Length != 16 || output.Length != 32)
        {
            throw new ArgumentException("Argon2id requires a 16-byte salt and 32-byte output in Vault format v1.");
        }

        Admission.Wait();
        try
        {
            fixed (byte* passwordPointer = password)
            fixed (byte* saltPointer = salt)
            fixed (byte* outputPointer = output)
            {
                var result = derive(
                    checked((uint)parameters.Iterations),
                    checked((uint)parameters.MemoryKiB),
                    checked((uint)parameters.Lanes),
                    passwordPointer,
                    (nuint)password.Length,
                    saltPointer,
                    (nuint)salt.Length,
                    outputPointer,
                    (nuint)output.Length);
                if (result != 0)
                {
                    throw new CryptographicException($"libargon2 rejected the derivation with code {result}.");
                }
            }
        }
        finally { Admission.Release(); }
    }

    public void Dispose() => NativeLibrary.Free(library);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int Argon2IdHashRaw(
        uint iterations,
        uint memoryKiB,
        uint lanes,
        byte* password,
        nuint passwordLength,
        byte* salt,
        nuint saltLength,
        byte* output,
        nuint outputLength);
}

internal sealed class VaultFormatException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
