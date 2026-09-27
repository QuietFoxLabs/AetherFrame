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
/// then on every write of the same file fails, for as long as the game runs. This store notices
/// exactly that — a write failing while <c>{path}.tmp</c> is held open by a handle that allows no
/// sharing (see <see cref="IsStuckTemporaryFile"/>) — and writes the file itself instead,
/// atomically and under a temporary name of its own (see <see cref="SystemFileStore.WriteAtomically"/>).
/// The stuck temporary file is left alone (its handle is still open), and any other failure is
/// passed through unchanged: a temporary file nobody holds open, an abandoned operation, a
/// disposed storage. If the direct write fails too, that failure is thrown.
///
/// <para>Caveat: a file written this way is not journaled, so Dalamud's backup copy of it stays
/// the one from the last successful reliable write until the next one succeeds. That is safe
/// because reads never prefer the backup (see <see cref="ReliableReads"/>): the on-disk file is
/// read first and used when intact; only content the reader rejects falls back to the backup, and
/// such a read is reported by the loaders (see <c>VersionedReadResult.RecoveredFromBackup</c>),
/// which keep a Recovery copy of the newer on-disk bytes before using it; and a file that merely
/// can't be read (locked, missing) is unavailable, not replaced.</para>
/// </summary>
internal sealed class StuckTempFallbackFileStore : IPlateFileStore
{
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
            SystemFileStore.WriteAtomically(path, Encoding.UTF8.GetBytes(contents));
            ReportOnce(path);
        }
    }

    public void MoveFile(string sourcePath, string destinationPath) => inner.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => inner.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    /// <summary>
    /// Whether a write failure is the leaked handle's: a Windows or I/O error while the reliable
    /// storage's own temporary file for <paramref name="path"/> is held open by a handle that
    /// allows no sharing. The error alone can't tell. With that handle open, Dalamud's CreateFile
    /// of the temporary file fails with a sharing violation and returns an invalid handle its null
    /// check doesn't catch, so what it throws is the error of writing through that handle:
    /// ERROR_INVALID_HANDLE (6), never the sharing violation (checked against Dalamud 15.0.3.5's
    /// FilesystemUtil.WriteAllBytesSafe). The temporary file itself is the evidence, so it is
    /// probed. Nothing else qualifies — not a temporary file nobody holds open (a leftover from a
    /// crash), not an abandoned operation, not a disposed storage — so the fallback can only ever
    /// replace a write that would keep failing for the same reason.
    /// </summary>
    internal static bool IsStuckTemporaryFile(Exception failure, string path) =>
        (failure is Win32Exception or IOException) && IsHeldOpenWithoutSharing(TemporaryPathFor(path));

    /// <summary>
    /// Whether another handle holds <paramref name="path"/> open with no sharing at all: even the
    /// most permissive open (read only, sharing everything) is then refused. False when the file
    /// is missing or opens.
    /// </summary>
    internal static bool IsHeldOpenWithoutSharing(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // A sharing or lock violation: the handle that left it behind is still open.
            return true;
        }
    }

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
            $"AetherFrame wrote \"{LogPrivacy.FileName(path)}\" directly because its temporary file is stuck open in the reliable storage; "
            + "the backup copy of that file is stale until its next save succeeds.");
    }
}
