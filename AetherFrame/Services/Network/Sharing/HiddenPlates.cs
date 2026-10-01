using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// The players whose Plates the player hid (C5's "hide"): kept on this PC only, by the name and
/// World the viewer looked them up by, and never sent anywhere. A hidden player's Plate is never
/// looked up until the player shows it again. The file is one small JSON object, read strictly and
/// replaced whole; one that can't be read hides nobody and is never written over, so nothing the
/// player hid is lost to a damaged file. Thread-safe. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class HiddenPlates
{
    /// <summary>The file's name in the plugin's configuration folder.</summary>
    internal const string FileName = "HiddenPlates.json";

    /// <summary>The most players kept hidden.</summary>
    internal const int MaxEntries = 1000;

    private const int MaxBytes = 128 * 1024;
    private const int Version = 1;

    private readonly string path;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly List<(string Name, string World)> entries = new();
    private bool loaded;
    private bool unreadable;

    internal HiddenPlates(string configDirectory, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        path = Path.Combine(Path.GetFullPath(configDirectory), FileName);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Whether the file couldn't be read: nothing is hidden, and nothing more can be until it is fixed or moved aside.</summary>
    internal bool Unreadable
    {
        get
        {
            lock (gate)
            {
                Load();
                return unreadable;
            }
        }
    }

    internal bool IsHidden(string name, string world)
    {
        lock (gate)
        {
            Load();
            return IndexOf(name, world) >= 0;
        }
    }

    /// <summary>Hides the player's Plate; false when it couldn't be saved, and then nothing changed.</summary>
    internal bool Hide(string name, string world)
    {
        lock (gate)
        {
            Load();
            if (IndexOf(name, world) >= 0)
            {
                return true;
            }

            if (unreadable || entries.Count >= MaxEntries)
            {
                return false;
            }

            entries.Add((name, world));
            if (Save())
            {
                return true;
            }

            entries.RemoveAt(entries.Count - 1);
            return false;
        }
    }

    /// <summary>Shows the player's Plate again; false when that couldn't be saved, and then nothing changed.</summary>
    internal bool Show(string name, string world)
    {
        lock (gate)
        {
            Load();
            var index = IndexOf(name, world);
            if (index < 0)
            {
                return true;
            }

            var removed = entries[index];
            entries.RemoveAt(index);
            if (Save())
            {
                return true;
            }

            entries.Insert(index, removed);
            return false;
        }
    }

    /// <summary>
    /// Whether two names, or two Worlds, are the same: game names are ASCII, and compared without
    /// regard to case, as the server compares them.
    /// </summary>
    internal static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private int IndexOf(string name, string world) => entries.FindIndex(entry => Same(entry.Name, name) && Same(entry.World, world));

    private void Load()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes)
            {
                throw new InvalidDataException("The file is too large.");
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != Version)
            {
                throw new InvalidDataException("Not a version this build reads.");
            }

            foreach (var entry in root.GetProperty("hidden").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString();
                var world = entry.GetProperty("world").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world) || name.Length > 64 || world.Length > 64 || entries.Count >= MaxEntries)
                {
                    throw new InvalidDataException("An entry isn't a name and a World.");
                }

                entries.Add((name, world));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            entries.Clear();
            unreadable = true;
            log($"Sharing: the hidden players' file couldn't be read ({exception.GetType().Name}), so nobody is hidden.");
        }
    }

    private bool Save()
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteStartArray("hidden");
                foreach (var (name, world) in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteString("world", world);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Sharing: the hidden players' file couldn't be saved ({exception.GetType().Name}).");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left for the next save to replace.
            }

            return false;
        }
    }
}
