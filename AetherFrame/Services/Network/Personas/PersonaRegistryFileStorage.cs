using System;
using System.IO;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;

namespace AetherFrame.Services.Network.Personas;

/// <summary>
/// The persona registry as one file of the plugin's own (docs/networking/DecisionRegister.md, P3):
/// <c>registry.afpr</c> in the networking directory, beside the key files' directory and never in
/// it, and never the plugin configuration or Dalamud's reliable storage. Compiled only in the
/// networking preview flavour, like everything under Services/Network.
/// <para>
/// <see cref="Replace"/> writes a temporary file beside the registry (<c>registry.afpr.tmp</c>,
/// replacing one an interrupted replace left), flushes it to disk, reads it back and compares, and
/// only then moves it over the registry, with the move written through
/// (<see cref="WrittenThroughMove.Replace"/>). So the registry holds the old bytes or the new ones,
/// never a mix and never nothing. <see cref="Read"/> never reads the temporary file.
/// </para>
/// </summary>
public sealed class PersonaRegistryFileStorage : IPersonaRegistryStorage
{
    /// <summary>The registry's name in its directory.</summary>
    public const string RegistryName = "registry.afpr";

    private const string TemporarySuffix = ".tmp";

    private readonly string directory;

    /// <summary>Storage in <paramref name="directory"/>, which is created on the first save.</summary>
    public PersonaRegistryFileStorage(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    /// <summary>The directory the registry lives in.</summary>
    public string Directory => directory;

    private string Registry => Path.Combine(directory, RegistryName);

    /// <inheritdoc />
    /// <remarks>
    /// Null only when the file or its directory doesn't exist. It reads at most one byte more than
    /// the largest registry, and throws when there is more: a larger file is not a registry this
    /// build reads, and is refused rather than read whole.
    /// </remarks>
    public byte[]? Read()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(Registry, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            var buffer = new byte[PersonaManager.MaxRegistryBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            if (total > PersonaManager.MaxRegistryBytes)
            {
                throw new IOException("The persona registry is larger than any registry this build reads.");
            }

            return buffer.AsSpan(0, total).ToArray();
        }
    }

    /// <inheritdoc />
    public void Replace(ReadOnlySpan<byte> bytes)
    {
        var final = Registry;
        var temporary = final + TemporarySuffix;
        System.IO.Directory.CreateDirectory(directory);
        var copy = bytes.ToArray();
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(copy);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                var check = new byte[copy.Length];
                stream.ReadExactly(check);
                if (stream.Length != copy.Length || !check.AsSpan().SequenceEqual(copy))
                {
                    throw new IOException("The persona registry's temporary file did not read back as written.");
                }
            }

            WrittenThroughMove.Replace(temporary, final);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
