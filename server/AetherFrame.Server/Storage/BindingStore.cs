using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Server.Lodestone;
using Microsoft.Data.Sqlite;

namespace AetherFrame.Server.Storage;

/// <summary>A key's binding to a character (decisions C1 and C4).</summary>
internal sealed record Binding(PersonaId Persona, long LodestoneId, string Name, string World, ProfileId ProfileId, bool Hidden);

/// <summary>What a successful check did: the binding's profile id.</summary>
internal sealed record BindResult(ProfileId ProfileId);

/// <summary>What a re-read did to a binding.</summary>
internal enum RereadResult
{
    /// <summary>The binding holds the name and World the page shows.</summary>
    Updated,

    /// <summary>The first "not found" page: the binding stays until a second one a day later.</summary>
    NotFoundOnce,

    /// <summary>A second "not found" page at least a day after the first: the binding and its content are gone.</summary>
    Removed,

    /// <summary>The key has no binding (any more).</summary>
    NotBound,
}

/// <summary>
/// Lodestone codes and bindings (decisions C1, C2 and C4). A key binds one character; a Lodestone id
/// is bound to one key; a canonical (name, World) is shown for one binding at a time. The only time
/// kept is a code's expiry, the day number (UTC) of each binding's last successful Lodestone read,
/// and, while a page shows "not found", the day it first did.
/// </summary>
internal sealed class BindingStore(ServerDatabase database, TimeProvider time)
{
    /// <summary>How long a code is valid (decision C2).</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How many day numbers a binding answers lookups for after its last successful read ("Checking a
    /// character through the player's own connection"), once no operator relay is set: read on day D,
    /// it answers through day D + 29, so for 30 days at most and 29 at least, then is hidden from
    /// lookups until its next successful read. Its key still finds it, to re-read it, publish and opt
    /// out. Its purpose is to bound how long a renamed, transferred or deleted character's Plate keeps
    /// answering under its old name and World, once the daily re-read no longer runs. The 30 is
    /// provisional: Claude chose it under the September 29, 2026 delegation, the owner was told on
    /// October 3, 2026 and hasn't approved it, and it awaits GPT's product review.
    /// </summary>
    public const int ReadWithinDays = 30;

    private long Now => time.GetUtcNow().ToUnixTimeSeconds();

    private long Today => Now / 86_400;

    /// <summary>Issues a code for <paramref name="persona"/>, replacing its earlier one.</summary>
    public async Task<string> IssueCodeAsync(PersonaId persona, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        while (true)
        {
            var code = LodestoneCodes.NewCode();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM codes WHERE expires <= $now; INSERT INTO codes (persona, code, expires) VALUES ($persona, $code, $expires) ON CONFLICT (persona) DO UPDATE SET code = excluded.code, expires = excluded.expires;";
            command.Parameters.AddWithValue("$now", Now);
            command.Parameters.AddWithValue("$persona", persona.ToString());
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$expires", Now + (long)CodeLifetime.TotalSeconds);
            try
            {
                await command.ExecuteNonQueryAsync(cancellation);
                return code;
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                // Another key's live code is the same 50 bits: draw again.
            }
        }
    }

    /// <summary>Whether <paramref name="code"/> is <paramref name="persona"/>'s live code. Nothing is consumed.</summary>
    public async Task<bool> HasCodeAsync(PersonaId persona, string code, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM codes WHERE persona = $persona AND code = $code AND expires > $now;";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$now", Now);
        return await command.ExecuteScalarAsync(cancellation) is not null;
    }

    public async Task<Binding?> FindByPersonaAsync(PersonaId persona, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        return await FindAsync(connection, null, "persona = $value", persona.ToString(), cancellation);
    }

    /// <summary>
    /// The binding a lookup of a canonical name and World finds, if any: shown (no newer read holds
    /// the name and World), and, when <paramref name="hideUnread"/> is set, read within
    /// <see cref="ReadWithinDays"/>. Everything that finds a binding for a viewer comes here, and
    /// says which: <see cref="Endpoints.Viewing"/> sets <paramref name="hideUnread"/> only while no
    /// operator relay is set, since the daily re-read keeps the day of the last read only while a
    /// relay is open, and an operator's relay is open only some of the time. A binding's own key
    /// finds it by <see cref="FindByPersonaAsync"/>.
    /// </summary>
    public async Task<Binding?> FindShownAsync(string nameKey, string world, bool hideUnread, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = hideUnread
            ? "SELECT persona, lodestone_id, name, world, profile_id, hidden FROM bindings WHERE name_key = $name AND world = $world AND hidden = 0 AND read_day > $stale;"
            : "SELECT persona, lodestone_id, name, world, profile_id, hidden FROM bindings WHERE name_key = $name AND world = $world AND hidden = 0;";
        command.Parameters.AddWithValue("$name", nameKey);
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$stale", Today - ReadWithinDays);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation) ? Read(reader) : null;
    }

    /// <summary>Every binding's key, in Lodestone id order, for the daily re-read.</summary>
    public async Task<IReadOnlyList<PersonaId>> ListPersonasAsync(CancellationToken cancellation)
    {
        var list = new List<PersonaId>();
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT persona FROM bindings ORDER BY lodestone_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            list.Add(PersonaId.Parse(reader.GetString(0)));
        }

        return list;
    }

    /// <summary>
    /// Binds <paramref name="lodestoneId"/> to <paramref name="persona"/> after a check matched its
    /// code, in one transaction (decisions C1 and C4):
    /// <list type="bullet">
    /// <item>a key already bound to another character is refused (null): one key, one character;</item>
    /// <item>a check by the key already bound refreshes the name and World and keeps the profile id;</item>
    /// <item>a character bound to another key moves to this one, and everything the old key published for it is deleted;</item>
    /// <item>another binding shown under the same name and World is hidden, not deleted;</item>
    /// <item>the day of the last successful read is today;</item>
    /// <item>the code is used up.</item>
    /// </list>
    /// </summary>
    public async Task<BindResult?> BindCharacterAsync(PersonaId persona, long lodestoneId, LodestoneCharacter character, CancellationToken cancellation)
    {
        var nameKey = CharacterNames.Key(character.Name)!;
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);

        var own = await FindAsync(connection, transaction, "persona = $value", persona.ToString(), cancellation);
        if (own is not null && own.LodestoneId != lodestoneId)
        {
            return null;
        }

        var deleted = false;
        var holder = await FindAsync(connection, transaction, "lodestone_id = $value", lodestoneId, cancellation);
        if (holder is not null && holder.Persona != persona)
        {
            deleted = await DeleteAsync(connection, transaction, holder.Persona, cancellation);
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR IGNORE INTO taken_over (persona) VALUES ($persona);";
            mark.Parameters.AddWithValue("$persona", holder.Persona.ToString());
            await mark.ExecuteNonQueryAsync(cancellation);
        }

        await HideOthersAsync(connection, transaction, nameKey, character.World, lodestoneId, cancellation);

        var profileId = own?.ProfileId ?? ProfileId.NewId();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO bindings (persona, lodestone_id, name, name_key, world, profile_id, hidden, not_found_day, read_day)
                VALUES ($persona, $id, $name, $key, $world, $profile, 0, NULL, $today)
                ON CONFLICT (persona) DO UPDATE SET name = excluded.name, name_key = excluded.name_key, world = excluded.world, hidden = 0, not_found_day = NULL, read_day = excluded.read_day;
                DELETE FROM codes WHERE persona = $persona;
                DELETE FROM taken_over WHERE persona = $persona;
                """;
            command.Parameters.AddWithValue("$persona", persona.ToString());
            command.Parameters.AddWithValue("$id", lodestoneId);
            command.Parameters.AddWithValue("$name", character.Name);
            command.Parameters.AddWithValue("$key", nameKey);
            command.Parameters.AddWithValue("$world", character.World);
            command.Parameters.AddWithValue("$profile", profileId.ToString());
            command.Parameters.AddWithValue("$today", Today);
            await command.ExecuteNonQueryAsync(cancellation);
        }

        await transaction.CommitAsync(cancellation);
        if (deleted)
        {
            await database.CheckpointAsync(CancellationToken.None);
        }

        return new BindResult(profileId);
    }

    /// <summary>
    /// Applies a re-read of <paramref name="lodestoneId"/>'s page (decision C1). A page that was read
    /// updates the name and World, shows the binding (hiding any other shown under the same name and
    /// World: the newest read wins), makes today the day of its last successful read, and forgets any
    /// "not found". The Lodestone's own "not found" page removes the binding, as opting out does, only
    /// on a read two or more calendar days after the first: at least 24 hours later, and at most 48,
    /// with only a day number kept. Any other failure changes nothing, and isn't passed here. A read
    /// of another character than the one the key's binding holds now (the key opted out and bound
    /// again during the fetch) changes nothing.
    /// </summary>
    public async Task<RereadResult> ApplyRereadAsync(PersonaId persona, long lodestoneId, LodestoneCharacter? character, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);
        var binding = await FindAsync(connection, transaction, "persona = $value", persona.ToString(), cancellation);
        if (binding is null || binding.LodestoneId != lodestoneId)
        {
            return RereadResult.NotBound;
        }

        if (character is not null)
        {
            var nameKey = CharacterNames.Key(character.Name)!;
            await HideOthersAsync(connection, transaction, nameKey, character.World, binding.LodestoneId, cancellation);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE bindings SET name = $name, name_key = $key, world = $world, hidden = 0, not_found_day = NULL, read_day = $today WHERE persona = $persona;";
            command.Parameters.AddWithValue("$name", character.Name);
            command.Parameters.AddWithValue("$key", nameKey);
            command.Parameters.AddWithValue("$world", character.World);
            command.Parameters.AddWithValue("$today", Today);
            command.Parameters.AddWithValue("$persona", persona.ToString());
            await command.ExecuteNonQueryAsync(cancellation);
            await transaction.CommitAsync(cancellation);
            return RereadResult.Updated;
        }

        long? firstDay;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT not_found_day FROM bindings WHERE persona = $persona;";
            query.Parameters.AddWithValue("$persona", persona.ToString());
            firstDay = await query.ExecuteScalarAsync(cancellation) is long day ? day : null;
        }

        if (firstDay is { } first && Today - first >= 2)
        {
            await DeleteAsync(connection, transaction, persona, cancellation);
            await transaction.CommitAsync(cancellation);
            await database.CheckpointAsync(CancellationToken.None);
            return RereadResult.Removed;
        }

        if (firstDay is null)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE bindings SET not_found_day = $day WHERE persona = $persona;";
            mark.Parameters.AddWithValue("$day", Today);
            mark.Parameters.AddWithValue("$persona", persona.ToString());
            await mark.ExecuteNonQueryAsync(cancellation);
        }

        await transaction.CommitAsync(cancellation);
        return RereadResult.NotFoundOnce;
    }

    /// <summary>
    /// Whether another key's check took <paramref name="persona"/>'s character (decision C1), so its
    /// plugin can say so. Kept only until the key binds again or opts out.
    /// </summary>
    public async Task<bool> WasTakenOverAsync(PersonaId persona, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM taken_over WHERE persona = $persona;";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        return await command.ExecuteScalarAsync(cancellation) is not null;
    }

    /// <summary>
    /// Opting out (decision C4): deletes the key's binding, code, latest revision, images and
    /// revision records at once, then checkpoints. True when anything was bound.
    /// </summary>
    public async Task<bool> OptOutAsync(PersonaId persona, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);
        var deleted = await DeleteAsync(connection, transaction, persona, cancellation);
        await transaction.CommitAsync(cancellation);
        if (deleted)
        {
            await database.CheckpointAsync(CancellationToken.None);
        }

        return deleted;
    }

    /// <summary>
    /// The operator's removal of a character (decisions S3 and C4): its binding and everything
    /// published for it, deleted as opting out deletes them. True when the id was bound.
    /// </summary>
    public async Task<bool> RemoveCharacterAsync(long lodestoneId, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);
        var binding = await FindAsync(connection, transaction, "lodestone_id = $value", lodestoneId, cancellation);
        if (binding is null)
        {
            return false;
        }

        await DeleteAsync(connection, transaction, binding.Persona, cancellation);
        await transaction.CommitAsync(cancellation);
        await database.CheckpointAsync(CancellationToken.None);
        return true;
    }

    private static async Task<bool> DeleteAsync(SqliteConnection connection, SqliteTransaction transaction, PersonaId persona, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM images WHERE persona = $persona;
            DELETE FROM latest WHERE persona = $persona;
            DELETE FROM revisions WHERE persona = $persona;
            DELETE FROM codes WHERE persona = $persona;
            DELETE FROM taken_over WHERE persona = $persona;
            DELETE FROM bindings WHERE persona = $persona;
            """;
        command.Parameters.AddWithValue("$persona", persona.ToString());
        return await command.ExecuteNonQueryAsync(cancellation) > 0;
    }

    private static async Task HideOthersAsync(SqliteConnection connection, SqliteTransaction transaction, string nameKey, string world, long lodestoneId, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE bindings SET hidden = 1 WHERE name_key = $key AND world = $world AND hidden = 0 AND lodestone_id <> $id;";
        command.Parameters.AddWithValue("$key", nameKey);
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$id", lodestoneId);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private static async Task<Binding?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, string where, object value, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT persona, lodestone_id, name, world, profile_id, hidden FROM bindings WHERE " + where + ";";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation) ? Read(reader) : null;
    }

    private static Binding Read(SqliteDataReader reader) => new(
        PersonaId.Parse(reader.GetString(0)),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        ProfileId.Parse(reader.GetString(4)),
        reader.GetInt64(5) != 0);
}
