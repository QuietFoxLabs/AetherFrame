using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Services.Network.Personas;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>Where one character's sharing stands on this PC (decision batch C, C1 to C4).</summary>
internal enum SharingStage
{
    /// <summary>Sharing is off. The character keeps its key, so turning it on again reuses it.</summary>
    Off,

    /// <summary>The player agreed and the character has a key; the Lodestone check hasn't passed yet.</summary>
    Checking,

    /// <summary>The check passed: the server binds the character to its key, under a profile id.</summary>
    Shared,

    /// <summary>Bound, with nothing published: the player paused sharing (C3).</summary>
    Paused,

    /// <summary>Another AetherFrame's check took the character over (C1).</summary>
    TakenOver,
}

/// <summary>
/// One character's sharing on this PC: the game's Content ID (the local key, as for the Active
/// Plate's binding), the persona that is its key (V4, C1; a slot named by nothing about the
/// character), and, once bound, the Lodestone id, the profile id (C4), and the name and World the
/// Lodestone showed. A bound character whose key can't be opened may also carry a new key being
/// checked (<see cref="NewSlot"/>, <see cref="NewKey"/>): its binding stays recorded, under the old
/// key, until a check with the new one passes (C1). A shared character also records which Plate the
/// server shows for it (<see cref="PublishedPlate"/>), the one My Plates marks Shared (C3).
/// </summary>
internal sealed record SharingCharacter(
    ulong ContentId,
    PersonaSlotId Slot,
    PersonaId Key,
    SharingStage Stage,
    string? LodestoneId = null,
    ProfileId? ProfileId = null,
    string? Name = null,
    string? World = null,
    PersonaSlotId NewSlot = default,
    PersonaId? NewKey = null,
    Guid? PublishedPlate = null)
{
    /// <summary>Whether the server binds the character (shared or paused).</summary>
    internal bool IsBound => Stage is SharingStage.Shared or SharingStage.Paused;

    /// <summary>Whether a new key is being checked in place of one that can't be opened.</summary>
    internal bool ReplacingKey => !NewSlot.IsEmpty;

    /// <summary>Whether a Lodestone check is under way: a character not bound yet, or a new key.</summary>
    internal bool Checking => Stage == SharingStage.Checking || ReplacingKey;

    /// <summary>The key a code and a check are for: the new one while it replaces the old.</summary>
    internal PersonaSlotId CheckingSlot => ReplacingKey ? NewSlot : Slot;

    /// <summary>The identity of <see cref="CheckingSlot"/>'s key.</summary>
    internal PersonaId CheckingKey => NewKey ?? Key;

    /// <summary>The same character with nothing bound, keeping its key.</summary>
    internal SharingCharacter Unbound(SharingStage stage) =>
        this with { Stage = stage, LodestoneId = null, ProfileId = null, Name = null, World = null, NewSlot = default, NewKey = null, PublishedPlate = null };
}

/// <summary>
/// The sharing file's bytes (<see cref="SharingStateFile"/>): one JSON object, read strictly. Every
/// property is required, none may repeat, and nothing else may appear; a bound character carries
/// its Lodestone id, profile id, name and World, and any other carries none of them.
/// </summary>
internal static class SharingStateCodec
{
    /// <summary>The largest file read.</summary>
    internal const int MaxBytes = 65536;

    /// <summary>The most characters the file holds.</summary>
    internal const int MaxCharacters = 64;

    /// <summary>The longest name or World kept, in UTF-16 code units.</summary>
    internal const int MaxTextLength = 64;

    /// <summary>The version this build writes: 2 adds the Plate the server shows. Version 1, which N2-9b wrote, is still read.</summary>
    private const int Version = 2;

    private static readonly string[] RootProperties = ["version", "characters"];
    private static readonly string[] CharacterPropertiesV1 = ["contentId", "slot", "key", "stage", "lodestoneId", "profileId", "name", "world", "newSlot", "newKey"];
    private static readonly string[] CharacterProperties = [.. CharacterPropertiesV1, "publishedPlate"];

    internal static byte[] Encode(IReadOnlyList<SharingCharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        Validate(characters);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("characters");
            foreach (var character in characters)
            {
                writer.WriteStartObject();
                writer.WriteString("contentId", character.ContentId.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("slot", character.Slot.ToString());
                writer.WriteString("key", character.Key.ToString());
                writer.WriteString("stage", StageName(character.Stage));
                WriteOptional(writer, "lodestoneId", character.LodestoneId);
                WriteOptional(writer, "profileId", character.ProfileId?.ToString());
                WriteOptional(writer, "name", character.Name);
                WriteOptional(writer, "world", character.World);
                WriteOptional(writer, "newSlot", character.ReplacingKey ? character.NewSlot.ToString() : null);
                WriteOptional(writer, "newKey", character.NewKey?.ToString());
                WriteOptional(writer, "publishedPlate", character.PublishedPlate?.ToString("D"));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException("The sharing file would be larger than any this build reads.");
        }

        return bytes;
    }

    /// <exception cref="InvalidDataException">The bytes aren't a sharing file this build reads.</exception>
    internal static IReadOnlyList<SharingCharacter> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException("The sharing file is larger than any this build reads.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The sharing file isn't JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            RequireExactly(root, RootProperties);
            var version = root.GetProperty("version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (1 or Version))
            {
                throw new InvalidDataException("The sharing file has a version this build doesn't read.");
            }

            var list = root.GetProperty("characters");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxCharacters)
            {
                throw new InvalidDataException("The sharing file's characters aren't a list this build reads.");
            }

            var characters = new List<SharingCharacter>();
            foreach (var item in list.EnumerateArray())
            {
                characters.Add(ReadCharacter(item, number));
            }

            Validate(characters);
            return characters;
        }
    }

    internal static string StageName(SharingStage stage) => stage switch
    {
        SharingStage.Off => "off",
        SharingStage.Checking => "checking",
        SharingStage.Shared => "shared",
        SharingStage.Paused => "paused",
        SharingStage.TakenOver => "taken-over",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    /// <summary>A name or World as the server answered it: 1 to 64 code units, trimmed, with no control or format-breaking character.</summary>
    internal static bool IsText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || text.Trim().Length != text.Length)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsSurrogate(c) || c is (char)0x2028 or (char)0x2029)
            {
                return false;
            }
        }

        return true;
    }

    private static SharingCharacter ReadCharacter(JsonElement item, int version)
    {
        RequireExactly(item, version == 1 ? CharacterPropertiesV1 : CharacterProperties);
        var contentText = RequiredString(item, "contentId");
        if (contentText.Length is 0 or > 20 || !IsDigits(contentText) || (contentText.Length > 1 && contentText[0] == '0')
            || !ulong.TryParse(contentText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentId) || contentId == 0)
        {
            throw new InvalidDataException("A character's Content ID isn't one.");
        }

        if (!PersonaSlotId.TryParse(RequiredString(item, "slot"), out var slot) || slot.IsEmpty)
        {
            throw new InvalidDataException("A character's key slot isn't one.");
        }

        if (!PersonaId.TryParse(RequiredString(item, "key"), out var key))
        {
            throw new InvalidDataException("A character's key isn't a persona identity.");
        }

        var stage = RequiredString(item, "stage") switch
        {
            "off" => SharingStage.Off,
            "checking" => SharingStage.Checking,
            "shared" => SharingStage.Shared,
            "paused" => SharingStage.Paused,
            "taken-over" => SharingStage.TakenOver,
            _ => throw new InvalidDataException("A character's stage isn't one this build knows."),
        };

        ProfileId? profileId = null;
        if (OptionalString(item, "profileId") is { } profileText)
        {
            if (!ProfileId.TryParse(profileText, out var parsed))
            {
                throw new InvalidDataException("A character's profile id isn't one.");
            }

            profileId = parsed;
        }

        PersonaSlotId newSlot = default;
        if (OptionalString(item, "newSlot") is { } newSlotText && (!PersonaSlotId.TryParse(newSlotText, out newSlot) || newSlot.IsEmpty))
        {
            throw new InvalidDataException("A character's new key slot isn't one.");
        }

        PersonaId? newKey = null;
        if (OptionalString(item, "newKey") is { } newKeyText)
        {
            if (!PersonaId.TryParse(newKeyText, out var parsedKey))
            {
                throw new InvalidDataException("A character's new key isn't a persona identity.");
            }

            newKey = parsedKey;
        }

        Guid? publishedPlate = null;
        if (version > 1 && OptionalString(item, "publishedPlate") is { } plateText)
        {
            if (!Guid.TryParseExact(plateText, "D", out var plate) || plate == Guid.Empty)
            {
                throw new InvalidDataException("A character's shared Plate isn't one.");
            }

            publishedPlate = plate;
        }

        return new SharingCharacter(contentId, slot, key, stage, OptionalString(item, "lodestoneId"), profileId, OptionalString(item, "name"), OptionalString(item, "world"), newSlot, newKey, publishedPlate);
    }

    private static void Validate(IReadOnlyList<SharingCharacter> characters)
    {
        if (characters.Count > MaxCharacters)
        {
            throw new InvalidDataException("The sharing file holds more characters than this build keeps.");
        }

        var contentIds = new HashSet<ulong>();
        var slots = new HashSet<PersonaSlotId>();
        foreach (var character in characters)
        {
            if (character.ContentId == 0 || character.Slot.IsEmpty || !contentIds.Add(character.ContentId) || !slots.Add(character.Slot)
                || (character.ReplacingKey && !slots.Add(character.NewSlot)))
            {
                throw new InvalidDataException("The sharing file names a character or a key twice, or not at all.");
            }

            if (character.ReplacingKey != (character.NewKey is not null) || (character.ReplacingKey && !character.IsBound))
            {
                throw new InvalidDataException("A new key is named whole, and only for a bound character.");
            }

            var carries = character.LodestoneId is not null || character.ProfileId is not null || character.Name is not null || character.World is not null;
            if (character.IsBound && (!LodestoneAddress.IsId(character.LodestoneId) || character.ProfileId is null || !IsText(character.Name) || !IsText(character.World)))
            {
                throw new InvalidDataException("A bound character's Lodestone id, profile id, name or World is missing or isn't one.");
            }

            if (!character.IsBound && carries)
            {
                throw new InvalidDataException("A character that isn't bound carries a binding's details.");
            }

            if (character.PublishedPlate is { } plate && (character.Stage != SharingStage.Shared || plate == Guid.Empty))
            {
                throw new InvalidDataException("Only a shared character's Plate can be shown by the server.");
            }
        }
    }

    private static void RequireExactly(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The sharing file holds something that isn't an object where one belongs.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(names, property.Name) < 0 || !seen.Add(property.Name))
            {
                throw new InvalidDataException("The sharing file holds a property that is unknown or repeated.");
            }
        }

        if (seen.Count != names.Length)
        {
            throw new InvalidDataException("The sharing file is missing a property.");
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("A property that must be text isn't.");
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException("A property that must be text or null isn't."),
        };
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static bool IsDigits(string text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The sharing file, <c>sharing.afsh</c>, in the persona folder beside the registry, never in
/// <c>keys\</c>. It is written as the registry is: a temporary file beside it, flushed, read back and
/// compared, then moved into place written through, so it holds the old bytes or the new ones.
/// Every use runs as a persona-session operation, under the persona files' lock (P3).
/// </summary>
internal sealed class SharingStateFile
{
    /// <summary>The file's name in the persona folder.</summary>
    internal const string FileName = "sharing.afsh";

    private const string TemporarySuffix = ".tmp";

    private readonly string directory;

    internal SharingStateFile(string personasDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personasDirectory);
        directory = Path.GetFullPath(personasDirectory);
    }

    private string Target => Path.Combine(directory, FileName);

    /// <summary>The characters the file holds; none when there is no file.</summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="InvalidDataException">It isn't a sharing file this build reads.</exception>
    internal IReadOnlyList<SharingCharacter> Read()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<SharingCharacter>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<SharingCharacter>();
        }

        using (stream)
        {
            var buffer = new byte[SharingStateCodec.MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return SharingStateCodec.Decode(buffer.AsSpan(0, total));
        }
    }

    /// <summary>Puts <paramref name="characters"/> in place of the file, durably and whole.</summary>
    internal void Replace(IReadOnlyList<SharingCharacter> characters)
    {
        var bytes = SharingStateCodec.Encode(characters);
        var final = Target;
        var temporary = final + TemporarySuffix;
        Directory.CreateDirectory(directory);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                var check = new byte[bytes.Length];
                stream.ReadExactly(check);
                if (stream.Length != bytes.Length || !check.AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException("The sharing file's temporary file did not read back as written.");
                }
            }

            WrittenThroughMove.Replace(temporary, final);
        }
        catch
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

            throw;
        }
    }
}
