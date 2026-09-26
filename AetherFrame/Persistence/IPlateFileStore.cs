using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AetherFrame.Persistence;

/// <summary>
/// The file operations the Plate Library needs, so its logic runs identically against Dalamud's
/// <c>IReliableFileStorage</c> in game and plain files in tests.
///
/// Existence and enumeration are ALWAYS the files actually on disk. Dalamud's reliable storage
/// also answers "exists" (and serves reads) from its backup database for files that are gone
/// from disk, and it has no delete — so a Plate moved to the trash would otherwise come back.
/// Backups are only used for what they're for: recovering a file that is present but damaged.
/// </summary>
internal interface IPlateFileStore
{
    /// <summary>True only when the file is currently on disk.</summary>
    bool FileExists(string path);

    /// <summary>Files on disk matching <paramref name="searchPattern"/> (top level only); empty
    /// when the directory doesn't exist.</summary>
    IReadOnlyList<string> ListFiles(string directory, string searchPattern);

    /// <summary>
    /// Reads a text file and passes it to <paramref name="reader"/>. If the reader throws (the
    /// signal that the content is unusable), a store with backups retries with its backup copy,
    /// running the reader a second time, and throws <see cref="System.IO.InvalidDataException"/>
    /// when no usable copy exists. A failure to read the file itself (missing, locked, access
    /// denied) is thrown as it is and never answered from a backup: the file is unavailable, not
    /// damaged.
    /// </summary>
    Task ReadTextAsync(string path, Action<string> reader);

    /// <summary>Atomically replaces the file's content (creating its directory as needed).</summary>
    Task WriteTextAsync(string path, string contents);

    /// <summary>Moves a file on disk. Never overwrites: throws if the destination exists.</summary>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>Copies a file on disk. Never overwrites: throws if the destination exists.</summary>
    void CopyFile(string sourcePath, string destinationPath);

    void DeleteFile(string path);
}
