using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AetherFrame.Persistence;

/// <summary>
/// Plain file-system <see cref="IPlateFileStore"/> with no backup database: writes go to a
/// temporary sibling file that then atomically replaces the target, so a crash mid-write leaves
/// either the old or the new content, never a torn file.
/// </summary>
internal sealed class SystemFileStore : IPlateFileStore
{
    private readonly Func<string, FileStream> createCopy;

    public SystemFileStore()
        : this(null)
    {
    }

    /// <param name="createCopy">Creates the new file a copy's bytes are written to (always a
    /// fresh temporary name, never an existing file). Only tests pass one: a stream that fails
    /// partway, as a full disk does. Null creates it with <see cref="FileMode.CreateNew"/>.</param>
    internal SystemFileStore(Func<string, FileStream>? createCopy)
    {
        this.createCopy = createCopy ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
    }

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : [];

    public async Task ReadTextAsync(string path, Action<StoredText> reader)
    {
        var text = await StoredTextDecoder.ReadFileAsync(path).ConfigureAwait(false);
        reader(text);
    }

    public Task WriteTextAsync(string path, string contents)
    {
        WriteAtomically(path, Encoding.UTF8.GetBytes(contents));
        return Task.CompletedTask;
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(sourcePath, destinationPath, overwrite: false);
    }

    /// <summary>
    /// Copies and flushes the copy to disk, and only then gives it its name. Every copy the
    /// Libraries make is a Recovery copy of a file about to be written over or a backup taken
    /// before a migration, made right before the original is replaced, and a copy under that name
    /// is the Libraries' only evidence that the original is safe. So the bytes go to a temporary
    /// sibling first, which is flushed (<see cref="File.Copy(string, string, bool)"/> may leave
    /// them in the system's cache) and then renamed to <paramref name="destinationPath"/>, never
    /// over an existing file: a copy that fails partway (a full disk), or a crash, can never leave
    /// a partial file under the copy's name, and a failed copy's temporary file is removed again.
    /// Like <c>File.Copy</c>, it keeps the source's modified time (for a damaged file, the evidence
    /// of whether it is newer than the storage's backup) and lets other programs keep the source open.
    /// </summary>
    /// <exception cref="IOException">The destination exists already, or the copy failed; nothing
    /// is left at the destination, and the source is never changed.</exception>
    public void CopyFile(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (File.Exists(destinationPath))
        {
            // Checked first so nothing is copied in vain; the rename below is what guarantees it.
            throw new IOException($"The file '{destinationPath}' already exists.");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var copy = createCopy(temporaryPath))
            {
                source.CopyTo(copy);
                copy.Flush(flushToDisk: true);
                File.SetLastWriteTimeUtc(copy.SafeFileHandle, File.GetLastWriteTimeUtc(source.SafeFileHandle));
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            // Only ever the temporary file this call created: a pre-existing file is never touched.
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy's own outcome is the one worth reporting.
            }
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Write-then-rename, shared with the other plain-IO writers (asset metadata, thumbnails).</summary>
    internal static void WriteAtomically(string path, byte[] contents)
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

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
