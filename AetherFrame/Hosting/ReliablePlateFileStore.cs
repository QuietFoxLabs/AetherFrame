using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using Dalamud.Plugin.Services;
using Dalamud.Storage;

namespace AetherFrame.Hosting;

/// <summary>
/// <see cref="IPlateFileStore"/> over Dalamud's <see cref="IReliableFileStorage"/>: atomic,
/// journaled writes, and reads that fall back to the backup copy when a file is damaged.
/// Existence and enumeration deliberately use the real file system (see
/// <see cref="IPlateFileStore"/>): the service's own Exists also reports backups of files that
/// were intentionally moved away, such as a deleted Plate.
///
/// <para>Read failures follow the contract the Libraries classify by: content damage (Dalamud found
/// neither the file nor a backup of it that the reader accepts) is an <see cref="InvalidDataException"/>,
/// exactly what the reader itself throws in the plain-file store; anything else (an I/O or access
/// error, an abandoned operation) means the file is unavailable, not damaged.</para>
/// </summary>
internal sealed class ReliablePlateFileStore : IPlateFileStore
{
    private readonly IReliableFileStorage storage;
    private readonly SystemFileStore files = new();

    internal ReliablePlateFileStore(IReliableFileStorage storage)
    {
        this.storage = storage;
    }

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern) => files.ListFiles(directory, searchPattern);

    public async Task ReadTextAsync(string path, Action<string> reader)
    {
        try
        {
            await storage.ReadAllTextAsync(path, reader).ConfigureAwait(false);
        }
        catch (FileReadException ex)
        {
            // Dalamud's own type for "the file and its backup both failed the reader" — the same
            // verdict the reader gives directly when there is no backup to fall back to.
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    public Task WriteTextAsync(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return storage.WriteAllTextAsync(path, contents);
    }

    public void MoveFile(string sourcePath, string destinationPath) => files.MoveFile(sourcePath, destinationPath);

    public void CopyFile(string sourcePath, string destinationPath) => files.CopyFile(sourcePath, destinationPath);

    public void DeleteFile(string path) => files.DeleteFile(path);
}
