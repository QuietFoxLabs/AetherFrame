using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AetherFrame.Personas.Storage;

namespace AetherFrame.Services.Network.Personas;

/// <summary>
/// The key protector for native Windows (docs/networking/DecisionRegister.md, K2): DPAPI in
/// CurrentUser scope, called directly through crypt32 with UI forbidden and no prompt structure. The
/// optional entropy is the envelope's header, byte for byte, so a blob opens only under the slot,
/// protector and public key it was made for. Compiled only in the networking preview flavour, like
/// everything under Services/Network.
/// <para>
/// What it protects against, and what not: a key file copied to another account or another machine
/// does not open. Anything running as the same Windows user can open it, other plugins in the game
/// process included, and a roaming profile or a domain's backup key can recover it on another
/// machine (K2's rationale). It claims protection only when a blob it just made carries the Windows
/// DPAPI provider identifier (<see cref="CarriesWindowsProvider"/>, K3): Wine's DPAPI obfuscates
/// only, and writes its own marker instead.
/// </para>
/// <para>
/// Every buffer that held a secret is zeroed: the managed copy of the input before it is released,
/// and DPAPI's output before <c>LocalFree</c>. On a system without crypt32, <see cref="Protect"/>
/// throws and <see cref="Unprotect"/> returns null, as the protector contract says.
/// </para>
/// <para>
/// DPAPI opens a blob it made even with bytes appended after it, so a blob's bytes are not fixed by
/// DPAPI but by the envelope, which carries the blob's exact length; the store checks the key it
/// opens against the envelope's public key in any case. Appending needs write access to the key
/// file, which already means the same user, who can open the key anyway.
/// </para>
/// </summary>
public sealed class DpapiPersonaKeyProtector : IPersonaKeyProtector
{
    /// <summary>The protector id every envelope this protector makes carries.</summary>
    public const string ProtectorId = "windows.dpapi.currentuser.v1";

    /// <summary>The largest blob this protector accepts or returns: far above DPAPI's form of a 32-byte scalar.</summary>
    public const int MaxBlobLength = 16 * 1024;

    // CRYPTPROTECT_UI_FORBIDDEN: a call that would need to prompt fails instead.
    private const int UiForbidden = 0x1;

    // A DPAPI blob starts with dwVersion = 1 (little-endian) and the provider GUID
    // df9d8cd0-1501-11d1-8c7a-00c04fc297eb in its binary (mixed-endian) layout.
    private static readonly byte[] WindowsBlobPrefix =
    [
        0x01, 0x00, 0x00, 0x00,
        0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11, 0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
    ];

    /// <inheritdoc />
    public string Id => ProtectorId;

    /// <summary>
    /// True when <paramref name="blob"/> starts as a Windows DPAPI blob does: version 1 and the
    /// Windows provider's identifier. The only evidence this protector accepts that it runs on
    /// native Windows DPAPI (K3): never the operating system's name, a Wine version or an
    /// environment variable.
    /// </summary>
    public static bool CarriesWindowsProvider(ReadOnlySpan<byte> blob) => blob.StartsWith(WindowsBlobPrefix);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The secret or the context is empty.</exception>
    /// <exception cref="CryptographicException">DPAPI refused, or returned no usable blob.</exception>
    /// <exception cref="DllNotFoundException">The system has no crypt32.</exception>
    public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("There is no secret to protect.", nameof(secret));
        }

        if (context.IsEmpty)
        {
            throw new ArgumentException("A blob is always bound to a context.", nameof(context));
        }

        var input = secret.ToArray();
        var entropy = context.ToArray();
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = default(NativeMethods.DataBlob);
        try
        {
            var inputBlob = new NativeMethods.DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var entropyBlob = new NativeMethods.DataBlob(entropy.Length, entropyHandle.AddrOfPinnedObject());
            if (!NativeMethods.CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return CopyOut(output) ?? throw new CryptographicException("DPAPI returned no usable blob.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            inputHandle.Free();
            entropyHandle.Free();
            ZeroAndFree(output);
        }
    }

    /// <inheritdoc />
    public byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context)
    {
        if (blob.IsEmpty || blob.Length > MaxBlobLength || context.IsEmpty)
        {
            return null;
        }

        var input = blob.ToArray();
        var entropy = context.ToArray();
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = default(NativeMethods.DataBlob);
        try
        {
            var inputBlob = new NativeMethods.DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var entropyBlob = new NativeMethods.DataBlob(entropy.Length, entropyHandle.AddrOfPinnedObject());
            return NativeMethods.CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                ? CopyOut(output)
                : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            ZeroAndFree(output);
        }
    }

    /// <summary>A managed copy of DPAPI's output, or null when it is empty or larger than any blob this protector handles.</summary>
    private static byte[]? CopyOut(NativeMethods.DataBlob output)
    {
        if (output.Data == IntPtr.Zero || output.Length is <= 0 or > MaxBlobLength)
        {
            return null;
        }

        var copy = new byte[output.Length];
        Marshal.Copy(output.Data, copy, 0, output.Length);
        return copy;
    }

    /// <summary>Zeroes DPAPI's output buffer, then returns it to the system (K2: never <c>LocalFree</c> a secret unzeroed).</summary>
    private static void ZeroAndFree(NativeMethods.DataBlob output)
    {
        if (output.Data == IntPtr.Zero)
        {
            return;
        }

        if (output.Length > 0)
        {
            Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
        }

        NativeMethods.LocalFree(output.Data);
    }

    private static class NativeMethods
    {
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>DATA_BLOB: a byte count and a pointer, laid out as crypt32 expects.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DataBlob
        {
            internal int Length;
            internal IntPtr Data;

            internal DataBlob(int length, IntPtr data)
            {
                Length = length;
                Data = data;
            }
        }
    }
}
