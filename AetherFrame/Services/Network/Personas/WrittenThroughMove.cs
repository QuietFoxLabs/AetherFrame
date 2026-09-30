using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AetherFrame.Services.Network.Personas;

/// <summary>
/// Moves a file within its own directory and returns only once the move is on disk: the one native
/// call the persona files need (docs/networking/DecisionRegister.md, P3 and K2). .NET's
/// <see cref="File.Move(string, string, bool)"/> never writes a move through, and ReplaceFile's
/// write-through flag is documented as unsupported, so this calls kernel32's <c>MoveFileExW</c>
/// directly with <c>MOVEFILE_WRITE_THROUGH</c>, and never with <c>MOVEFILE_COPY_ALLOWED</c>: its
/// callers keep the source beside the destination, so a move is a rename and never a copy. On
/// NTFS a rename is one atomic step; on FAT or exFAT (a launcher on a USB drive) it may not be,
/// and Microsoft states the flush guarantee explicitly only for a move that copies and deletes.
/// P3 records both as remaining risks.
/// <para>
/// Paths get the extended-length prefix, since a direct call doesn't get .NET's long-path handling.
/// A sharing violation or a denied access is retried a few times, briefly: a virus scanner or an
/// indexer often opens a file just written. Any other failure throws at once.
/// </para>
/// <para>
/// Off Windows, both moves fall back to <see cref="File.Move(string, string, bool)"/>, which renames
/// atomically without the flush. That serves the persona suite's Linux run only and is not durable:
/// the DPAPI protector claims no protection there, so persona features never turn on (K3). On
/// Windows, and under Wine, where <see cref="OperatingSystem.IsWindows"/> is true and kernel32
/// provides the call, there is no fallback: a failure of the call itself throws.
/// </para>
/// </summary>
internal static class WrittenThroughMove
{
    private const int ReplaceExistingFlag = 0x1;
    private const int WriteThroughFlag = 0x8;

    private const int AccessDenied = 5;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int Retries = 3;
    private const int RetryDelayMilliseconds = 75;

    // Built from the separator rather than written out, so no tool can mangle the backslashes:
    // "\\?\" and "\\?\UNC\" on Windows, and "\\.\" for a device path that is left as it is.
    private static readonly string Separator = Path.DirectorySeparatorChar.ToString();
    private static readonly string ExtendedPrefix = Separator + Separator + "?" + Separator;
    private static readonly string ExtendedUncPrefix = ExtendedPrefix + "UNC" + Separator;
    private static readonly string DevicePrefix = Separator + Separator + "." + Separator;

    /// <summary>Puts <paramref name="source"/> in place of <paramref name="destination"/>, which may exist, written through.</summary>
    /// <exception cref="IOException">
    /// The move failed, and <paramref name="destination"/> normally holds what it held before. A
    /// failure reported after the rename took effect leaves the new bytes: P3's indeterminate save.
    /// </exception>
    internal static void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        Move(source, destination, ReplaceExistingFlag | WriteThroughFlag);
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/>, which must not exist yet, written through.</summary>
    /// <exception cref="IOException">The move failed, including because <paramref name="destination"/> exists; nothing was replaced.</exception>
    internal static void MoveNew(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        Move(source, destination, WriteThroughFlag);
    }

    /// <summary>The path as Windows' extended-length form, which a direct call needs for a path over 260 characters.</summary>
    internal static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || full.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(Separator + Separator, StringComparison.Ordinal)
            ? ExtendedUncPrefix + full[2..]
            : ExtendedPrefix + full;
    }

    private static void Move(string source, string destination, int flags)
    {
        var from = Extended(source);
        var to = Extended(destination);
        for (var attempt = 0; ; attempt++)
        {
            if (NativeMethods.MoveFileEx(from, to, flags))
            {
                return;
            }

            // The error's number only, never a path: the log names no paths.
            var error = Marshal.GetLastPInvokeError();
            if (attempt < Retries && error is AccessDenied or SharingViolation or LockViolation)
            {
                Thread.Sleep(RetryDelayMilliseconds);
                continue;
            }

            throw new IOException($"The file could not be moved (Windows error {error}).", unchecked((int)(0x80070000u | (uint)error)));
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
