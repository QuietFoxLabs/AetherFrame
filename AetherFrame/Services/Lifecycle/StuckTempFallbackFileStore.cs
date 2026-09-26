using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Lifecycle;

/// <summary>
/// Keeps a Plate saveable after Dalamud's reliable storage has leaked the handle of a file's
/// temporary sibling. Dalamud writes every file through <c>{path}.tmp</c> with an exclusive
/// handle, and when the write itself fails (a full disk, say) that handle is never closed: from
/// then on every write of the same file fails with a sharing violation on the same <c>.tmp</c>
/// name, for as long as the game runs. This store notices exactly that — a sharing or lock
/// violation while <c>{path}.tmp</c> is on disk — and writes the file itself instead, atomically
/// and under a temporary name of its own (see <see cref="SystemFileStore.WriteAtomically"/>). The
/// stuck temporary file is left alone (its handle is still open), and any other failure is
/// passed through unchanged: another Windows error, an abandoned operation, a disposed storage.
///
/// <para>Caveat: a file written this way is not journaled, so Dalamud's backup copy of it stays
/// the one from the last successful reliable write until the next one succeeds. A read that had to
/// fall back to that older backup is reported by the loaders (see
/// <c>VersionedReadResult.RecoveredFromBackup</c>), which keep a Recovery copy of the newer on-disk
/// bytes before using it, so the stale backup can never silently replace them.</para>
/// </summary>
internal sealed class StuckTempFallbackFileStore : IPlateFileStore
{
    /// <summary>ERROR_SHARING_VIOLATION: the temporary file is open with no sharing.</summary>
    internal const int SharingViolation = 32;

    /// <summary>ERROR_LOCK_VIOLATION: a byte range of the temporary file is locked.</summary>
    internal const int LockViolation = 33;

    /// <summary>The same two errors as .NET's file APIs report them, as an HRESULT.</summary>
    internal const int SharingViolationHResult = unchecked((int)0x80070020);
    internal const int LockViolationHResult = unchecked((int)0x80070021);

    private readonly IPlateFileStore inner;
    private readonly IAetherFrameLog log;
    private readonly HashSet<string> reportedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    internal StuckTempFallbackFileStore(IPlateFileStore inner, IAetherFrameLog log)
    {
        this.inner = inner;
        this.log = log;
    }

    public bool FileExists(string path) => inner.FileExists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => inner.ListFiles(directory, searchPattern);

    public Task ReadTextAsync(string path, Action<string> reader) => inner.ReadTextAsync(path, reader);

    public async Task WriteTextAsync(string path, string contents)
    {
        try
        {
            await inner.WriteTextAsync(path, contents).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStuckTemporaryFile(ex, path))
        {
            ReportOnce(path);
            SystemFileStore.WriteAtomically(path, Encoding.UTF8.GetBytes(contents));
        }
    }

    public void MoveFile(string sourcePath, string destinationPath) => inner.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => inner.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    /// <summary>
    /// Whether a write failure is a sharing or lock violation (a Win32 error 32 or 33, or an
    /// <see cref="IOException"/> carrying one as its HRESULT) while the reliable storage's own
    /// temporary file for <paramref name="path"/> exists. Nothing else qualifies — not a Windows
    /// error of another kind, not an abandoned operation, not a disposed storage — so the
    /// fallback can only ever replace a write that would keep failing for the same reason.
    /// </summary>
    internal static bool IsStuckTemporaryFile(Exception failure, string path) =>
        IsSharingOrLockViolation(failure) && File.Exists(TemporaryPathFor(path));

    /// <summary>Whether <paramref name="failure"/> is a Windows sharing or lock violation.</summary>
    internal static bool IsSharingOrLockViolation(Exception failure) => failure switch
    {
        Win32Exception win32 => win32.NativeErrorCode is SharingViolation or LockViolation,
        IOException io => io.HResult is SharingViolationHResult or LockViolationHResult,
        _ => false,
    };

    /// <summary>The temporary sibling Dalamud's reliable storage writes through.</summary>
    internal static string TemporaryPathFor(string path) => path + ".tmp";

    private void ReportOnce(string path)
    {
        lock (gate)
        {
            if (!reportedPaths.Add(path))
            {
                return;
            }
        }

        log.Warning(
            $"AetherFrame wrote \"{Path.GetFileName(path)}\" directly because its temporary file is stuck open in the reliable storage; "
            + "the backup copy of that file is stale until its next save succeeds.");
    }
}
