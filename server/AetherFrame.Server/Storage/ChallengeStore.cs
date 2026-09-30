using System;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;

namespace AetherFrame.Server.Storage;

/// <summary>
/// The challenges of the specification's section 13, rule 10: 32 bytes from the CSPRNG, accepted at
/// most once and only for 300 seconds after issue, consumed in one atomic step. They live in the
/// database, so a restart forgets none it issued and every instance serving the name shares them.
/// Never logged.
/// </summary>
internal sealed class ChallengeStore(ServerDatabase database, TimeProvider time)
{
    /// <summary>How long a challenge is accepted after issue (rule 10's baseline).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(300);

    /// <summary>Issues a fresh challenge, and clears expired ones.</summary>
    public async Task<RequestChallenge> IssueAsync(CancellationToken cancellation)
    {
        var challenge = RequestChallenge.NewRandom();
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM challenges WHERE expires <= $now; INSERT INTO challenges (value, expires) VALUES ($value, $expires);";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$value", challenge.ToArray());
        command.Parameters.AddWithValue("$expires", now + (long)Lifetime.TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellation);
        return challenge;
    }

    /// <summary>
    /// Whether <paramref name="challenge"/> is known, unexpired and unused, without consuming it: a
    /// publish's early check, before its body is read. Only <see cref="TryConsumeAsync"/> lets a
    /// request act.
    /// </summary>
    public async Task<bool> IsLiveAsync(RequestChallenge challenge, CancellationToken cancellation)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM challenges WHERE value = $value AND expires > $now;";
        command.Parameters.AddWithValue("$value", challenge.ToArray());
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteScalarAsync(cancellation) is not null;
    }

    /// <summary>
    /// Consumes <paramref name="challenge"/> in one statement: known, unexpired and unused, then
    /// removed. False when it is any of the three, and then nothing was consumed.
    /// </summary>
    public async Task<bool> TryConsumeAsync(RequestChallenge challenge, CancellationToken cancellation)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM challenges WHERE value = $value AND expires > $now;";
        command.Parameters.AddWithValue("$value", challenge.ToArray());
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteNonQueryAsync(cancellation) == 1;
    }
}
