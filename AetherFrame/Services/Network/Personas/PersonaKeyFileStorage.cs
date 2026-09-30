using System;
using System.IO;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;

namespace AetherFrame.Services.Network.Personas;

/// <summary>
/// The plugin's key blob storage: one directory of the plugin's own files, one <c>.afkey</c> file
/// per slot, named by the slot's text form and nothing else (docs/networking/NETWORK1.md, system 2:
/// never the plugin configuration, never Dalamud's reliable storage, and no persona identity in a
/// name). Compiled only in the networking preview flavour, like everything under Services/Network.
/// <para>
/// <see cref="WriteNew"/> writes a temporary file (<c>.afkey.tmp</c>) with create-new semantics,
/// flushes it to disk, reads it back and compares, then moves it into place without overwriting,
/// so the final name only ever holds bytes that were verified, or nothing. A temporary file left by
/// an interrupted write is deleted before the next write of that slot and is never trusted. Nothing
/// here deletes a key.
/// </para>
/// <para>
/// Durability: the file's bytes reach the disk before the move, and the move itself is written
/// through (<see cref="WrittenThroughMove.MoveNew"/>, <c>MoveFileExW</c> with
/// <c>MOVEFILE_WRITE_THROUGH</c>), so once this returns the key is under its final name on disk
/// before the persona registry records it (K2 and P3 in docs/networking/DecisionRegister.md).
/// </para>
/// </summary>
public sealed class PersonaKeyFileStorage : IPersonaKeyBlobStorage
{
    /// <summary>The extension of every key file.</summary>
    public const string Extension = ".afkey";

    private const string TemporarySuffix = ".tmp";

    // No envelope is anywhere near this; a larger file is not one and is refused rather than read.
    private const long MaxLength = 64 * 1024;

    private readonly string directory;

    /// <summary>Storage in <paramref name="directory"/>, which is created on the first write.</summary>
    public PersonaKeyFileStorage(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    /// <summary>The directory the key files live in.</summary>
    public string Directory => directory;

    /// <inheritdoc />
    public byte[]? Read(PersonaSlotId slot)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(FileFor(slot), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            if (stream.Length > MaxLength)
            {
                throw new IOException("The key file is larger than any key envelope.");
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    /// <inheritdoc />
    public void WriteNew(PersonaSlotId slot, ReadOnlySpan<byte> blob)
    {
        var final = FileFor(slot);
        var temporary = final + TemporarySuffix;
        System.IO.Directory.CreateDirectory(directory);
        if (File.Exists(final))
        {
            throw new IOException("A key is already held under that slot; nothing replaces it.");
        }

        File.Delete(temporary);
        var bytes = blob.ToArray();
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                var check = new byte[bytes.Length];
                stream.ReadExactly(check);
                if (stream.Length != bytes.Length || !check.AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException("The key file did not read back as written.");
                }
            }

            // No overwrite: a file that appeared under the final name meanwhile makes this throw.
            WrittenThroughMove.MoveNew(temporary, final);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists the directory's entries once: every <c>.afkey</c> file whose name is a slot's text form
    /// is a slot; any other entry (a temporary file from an interrupted write, a stray file or folder)
    /// is counted as skipped and never read. On Windows, where a name that differs only in case is
    /// the same file, a name is compared as <see cref="Read"/> would find it, so a key the store can
    /// open is never counted as a stray entry. A directory that does not exist yet holds nothing. Any
    /// other failure (access denied; on Windows, a file where the directory should be, which other
    /// systems report as not found) throws, so that it is reported as a failed listing and never
    /// taken for an empty one.
    /// </remarks>
    public PersonaKeyListing List()
    {
        var ignoreCase = OperatingSystem.IsWindows();
        var slots = new System.Collections.Generic.List<PersonaSlotId>();
        var skipped = 0;
        try
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.EndsWith(Extension, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && File.Exists(entry)
                    && PersonaSlotId.TryParse(ignoreCase ? name[..^Extension.Length].ToLowerInvariant() : name[..^Extension.Length], out var slot))
                {
                    slots.Add(slot);
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return new PersonaKeyListing(Array.Empty<PersonaSlotId>(), 0);
        }

        return new PersonaKeyListing(slots, skipped);
    }

    private string FileFor(PersonaSlotId slot)
    {
        if (slot.IsEmpty)
        {
            throw new ArgumentException("The empty slot never holds a key.", nameof(slot));
        }

        return Path.Combine(directory, slot.ToString() + Extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
