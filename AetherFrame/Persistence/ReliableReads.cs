using System;
using System.IO;
using System.Threading.Tasks;

namespace AetherFrame.Persistence;

/// <summary>
/// The read half of an <see cref="IPlateFileStore"/> over a storage that keeps backup copies
/// (Dalamud's reliable file storage, in game): the file on disk is read here, first, and the
/// backup is asked for only when the reader rejects what is on disk.
///
/// <para>Why the first read isn't left to the storage: its own first chance silently serves the
/// backup whenever the file is missing or fails to read (locked, access denied), with the reader
/// run once — indistinguishable, to the loaders, from the file itself. That is stale data with no
/// warning whenever the backup is older than the disk, which it is after every write that had to
/// bypass the storage's journal (see <c>StuckTempFallbackFileStore</c>). So an I/O failure here
/// stays an I/O failure, which the Libraries treat as "unavailable, never replaced"; only content
/// the reader rejects falls back to the backup, and then through the reader's second run, which
/// is how the loaders know to keep a Recovery copy of the on-disk bytes
/// (<see cref="VersionedReadResult{T}.RecoveredFromBackup"/>).</para>
/// </summary>
internal static class ReliableReads
{
    /// <param name="reader">Reads the text; throwing is the signal that the content is unusable.</param>
    /// <param name="readBackup">Reads the storage's backup copy of the file; throws
    /// <see cref="FileNotFoundException"/> when there is none.</param>
    /// <exception cref="InvalidDataException">The reader rejected the file and there is no backup,
    /// or it rejected the backup too: content damage, as the reader itself reports it. A failure
    /// to read the file (or the backup) at all is thrown as it is.</exception>
    internal static async Task ReadTextAsync(string path, Action<string> reader, Func<Task<string>> readBackup)
    {
        // Decoded as the plain-file store decodes it (UTF-8, a byte order mark honoured rather than
        // kept as text): what the storage writes has no mark, a hand-edited file may.
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);

        Exception rejected;
        try
        {
            reader(text);
            return;
        }
        catch (Exception ex)
        {
            rejected = ex;
        }

        string backup;
        try
        {
            backup = await readBackup().ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // No backup: the reader's verdict on the file stands.
            throw new InvalidDataException(rejected.Message, rejected);
        }

        try
        {
            reader(backup);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Neither '{Path.GetFileName(path)}' nor its backup copy could be read: {ex.Message}", ex);
        }
    }
}
