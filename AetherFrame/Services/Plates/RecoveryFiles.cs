using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AetherFrame.Persistence;

namespace AetherFrame.Services.Plates;

/// <summary>
/// The file operations recovery checkpoints need (see <see cref="RecoveryCheckpointStore"/>), so tests
/// can make any of them fail. Plain files, deliberately not Dalamud's reliable storage: that storage
/// keeps a backup row for every path it ever wrote and has no way to remove one, so a new checkpoint
/// name every few seconds would grow its database for as long as anyone edits. A checkpoint is made
/// safe instead by a flushed temporary file renamed into place, a read-back before anything older is
/// retired, and the older checkpoints kept beside it.
/// </summary>
internal interface IRecoveryFiles
{
    /// <summary>Files directly in <paramref name="directory"/> matching <paramref name="pattern"/>; empty when it doesn't exist.</summary>
    IReadOnlyList<string> ListFiles(string directory, string pattern);

    /// <summary>Folders directly in <paramref name="directory"/>; empty when it doesn't exist.</summary>
    IReadOnlyList<string> ListDirectories(string directory);

    /// <summary>
    /// Writes <paramref name="contents"/> under a new name: to a temporary sibling first, flushed to
    /// disk, then renamed to <paramref name="path"/>, never over a file. A failure leaves nothing under
    /// <paramref name="path"/>; an interruption can leave only the temporary file, which no reader takes for a checkpoint.
    /// </summary>
    void WriteNew(string path, byte[] contents);

    /// <summary>The file's text as it is on disk, and whether every byte was valid text.</summary>
    StoredText ReadText(string path);

    bool FileExists(string path);

    void DeleteFile(string path);

    /// <summary>Removes <paramref name="directory"/> only when nothing is in it; false otherwise.</summary>
    bool TryDeleteEmptyDirectory(string directory);

    DateTime GetLastWriteTimeUtc(string path);

    DateTime GetDirectoryCreationTimeUtc(string directory);

    /// <summary>
    /// Creates <paramref name="lockPath"/> (and its folder) and holds it open with no sharing, for as long
    /// as the returned handle lives: other game clients then see that run as still going. Throws when it can't.
    /// </summary>
    IDisposable HoldLock(string lockPath);

    /// <summary>Whether another open handle holds <paramref name="lockPath"/> now (a run still going); false when there is no such file.</summary>
    bool IsLockHeld(string lockPath);
}

/// <summary>The recovery checkpoints' plain files (see <see cref="IRecoveryFiles"/>).</summary>
internal sealed class SystemRecoveryFiles : IRecoveryFiles
{
    internal static readonly SystemRecoveryFiles Instance = new();

    public IReadOnlyList<string> ListFiles(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : [];

    public IReadOnlyList<string> ListDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.GetDirectories(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : [];

    public void WriteNew(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public StoredText ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return StoredTextDecoder.Decode(memory.ToArray());
    }

    public bool FileExists(string path) => File.Exists(path);

    public void DeleteFile(string path) => File.Delete(path);

    public bool TryDeleteEmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return false;
        }

        // Not recursive: a file that appears meanwhile makes this throw, and it stays.
        Directory.Delete(directory, recursive: false);
        return true;
    }

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public DateTime GetDirectoryCreationTimeUtc(string directory) => Directory.GetCreationTimeUtc(directory);

    public IDisposable HoldLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public bool IsLockHeld(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // A sharing violation: a running game client holds it.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Can't tell: treated as held, so nothing of that run is offered or touched.
            return true;
        }
    }
}
