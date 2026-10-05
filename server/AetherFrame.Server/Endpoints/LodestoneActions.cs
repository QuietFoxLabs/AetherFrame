using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Presence;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// The Lodestone check and re-read (decisions C1, C2, C6 and C8), shared by their two ways in. A
/// <c>POST</c> has the page read by the server's own client, through the operator's relay when one is
/// set, in the order N2-7b set. A WebSocket (<see cref="PipeSession"/>) has it read through the
/// player's own connection, in the order "Checking a character through the player's own connection"
/// sets: for a check, the page is read before the allowlist and the second-character rule, so
/// whether the pipe opens tells nothing about an id (ServerApi-v1.md, sections 2.1 and 2.3).
/// </summary>
internal sealed class LodestoneActions(RateLimiter limiter, BindingStore bindings, Allowlist allowlist, LodestoneReader lodestone, LodestoneBudget budget, IOptions<ServerOptions> options, PresenceStore presence)
{
    /// <summary>The reason a <c>503</c> carries when the Lodestone turned the player's own connection away.</summary>
    public const string RefusedReason = "lodestone:refused";

    private static readonly string[] CheckFields = ["lodestoneId", "code", "name", "world"];

    /// <summary>
    /// The check (decision C2): the code on the character's page binds the character to the signer's
    /// key. With <paramref name="pipe"/> null the page comes through the server's own client; with a
    /// pipe, through the player's connection.
    /// </summary>
    public async Task<ActionAnswer> CheckAsync(VerifiedAction action, IPAddress? address, PipeSession? pipe, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        long? readEnded = null;
        var persona = action.PublicKey.Id;

        // Every failure below is the same "check failed" (decision C2), answered no sooner than the
        // floor, and, after a read through a pipe, no sooner than a fixed delay after the read ended
        // too. So neither its answer nor its timing tells whether an id is on the allowlist; only
        // the log's kind differs.
        async Task<ActionAnswer> CheckFailedAsync(string kind)
        {
            var wait = options.Value.CheckFailureFloor - Stopwatch.GetElapsedTime(started);
            if (readEnded is { } ended)
            {
                var afterRead = options.Value.CheckFailureAfterRead - Stopwatch.GetElapsedTime(ended);
                if (afterRead > wait)
                {
                    wait = afterRead;
                }
            }

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellation);
            }

            return ActionAnswer.Fail(StatusCodes.Status422UnprocessableEntity, kind);
        }

        var body = ActionBody.Read(action.Body, CheckFields);
        if (body is null || !LodestoneIds.TryParse(body.String("lodestoneId"), out var lodestoneId) || !LodestoneCodes.IsWellFormed(body.String("code")))
        {
            return ActionAnswer.Fail(StatusCodes.Status400BadRequest, "body:json");
        }

        if (!limiter.TryTake(ServerLimits.LodestonePerKey, persona.ToString()) || !TakeCheckForId(lodestoneId, address))
        {
            return ActionAnswer.Fail(StatusCodes.Status429TooManyRequests, "limit:lodestone");
        }

        // "Try again later" is answered before anything that differs between ids. A piped read
        // spends none of the hour's budget.
        if (pipe is null && !budget.HasRoom(reread: false))
        {
            return ActionAnswer.Fail(StatusCodes.Status503ServiceUnavailable, "check:busy");
        }

        var code = body.String("code");
        if (!await bindings.HasCodeAsync(persona, code, cancellation))
        {
            return await CheckFailedAsync("check:code");
        }

        LodestoneRead read;
        if (pipe is null)
        {
            if (!allowlist.Allows(lodestoneId))
            {
                return await CheckFailedAsync("check:allowlist");
            }

            if (await bindings.FindByPersonaAsync(persona, cancellation) is { } own && own.LodestoneId != lodestoneId)
            {
                return await CheckFailedAsync("check:second-character");
            }

            read = await lodestone.ReadAsync(lodestoneId, reread: false, cancellation);
            if (read.Outcome == LodestoneOutcome.Busy)
            {
                return ActionAnswer.Fail(StatusCodes.Status503ServiceUnavailable, "check:busy");
            }
        }
        else
        {
            // The pipe opens for every id that gets this far, and the read itself never depends on
            // the allowlist: what fails it ("try again later", or the Lodestone refusing the
            // player's connection) is answered at once.
            read = await pipe.ReadAsync(lodestoneId, cancellation);
            readEnded = Stopwatch.GetTimestamp();
            if (PipeFailure(read, "check:") is { } failure)
            {
                return failure;
            }

            if (!allowlist.Allows(lodestoneId))
            {
                return await CheckFailedAsync("check:allowlist");
            }

            if (await bindings.FindByPersonaAsync(persona, cancellation) is { } own && own.LodestoneId != lodestoneId)
            {
                return await CheckFailedAsync("check:second-character");
            }
        }

        var character = read.Character;
        if (character is null || !character.SelfIntroduction.Contains(code, StringComparison.Ordinal))
        {
            return await CheckFailedAsync("check:page");
        }

        // The page must be the character the plugin is logged in as (N2-9b): a player who
        // pastes another of their characters' pages binds nothing, and takes nothing over.
        if (CharacterNames.Key(body.String("name")) is not { } claimed || claimed != CharacterNames.Key(character.Name)
            || !string.Equals(body.String("world"), character.World, StringComparison.OrdinalIgnoreCase))
        {
            return await CheckFailedAsync("check:other-character");
        }

        var bound = await bindings.BindCharacterAsync(persona, lodestoneId, character, cancellation);
        if (bound is null)
        {
            return await CheckFailedAsync("check:second-character");
        }

        // A takeover (C1): the old key's presence sessions stop counting the character at once.
        presence.ForgetOtherKeys(lodestoneId, persona);

        // The day as the binding's own transaction stored it; a POST's body never carries it.
        return ActionAnswer.Ok(new CharacterEndpoints.CheckAnswer(bound.ProfileId.ToString(), character.Name, character.World)) with { ReadDay = bound.ReadDay };
    }

    /// <summary>
    /// The re-read of the signer's own binding (decision C1): the name and World the page shows now,
    /// or, on the Lodestone's own "not found" page twice a day apart, the binding removed.
    /// </summary>
    public async Task<ActionAnswer> RereadAsync(VerifiedAction action, PipeSession? pipe, CancellationToken cancellation)
    {
        var persona = action.PublicKey.Id;
        if (ActionBody.Read(action.Body, []) is null)
        {
            return ActionAnswer.Fail(StatusCodes.Status400BadRequest, "body:json");
        }

        if (!limiter.TryTake(ServerLimits.LodestonePerKey, persona.ToString()))
        {
            return ActionAnswer.Fail(StatusCodes.Status429TooManyRequests, "limit:lodestone/key");
        }

        var binding = await bindings.FindByPersonaAsync(persona, cancellation);
        if (binding is null && await bindings.WasTakenOverAsync(persona, cancellation))
        {
            return ActionAnswer.Fail(StatusCodes.Status410Gone, "reread:taken-over");
        }

        if (binding is null || !allowlist.Allows(binding.LodestoneId))
        {
            return ActionAnswer.Fail(StatusCodes.Status404NotFound, "reread:not-bound");
        }

        var read = pipe is null
            ? await lodestone.ReadAsync(binding.LodestoneId, reread: false, cancellation)
            : await pipe.ReadAsync(binding.LodestoneId, cancellation);
        if (read.Outcome == LodestoneOutcome.Refused)
        {
            return new ActionAnswer(StatusCodes.Status503ServiceUnavailable, "reread:" + read.Outcome, Reason: RefusedReason);
        }

        // Only a page that was read, or the Lodestone's own "not found" page, changes the binding: a
        // dropped pipe, a failed handshake or a cut-off page never counts as "not found" (C1).
        if (read.Outcome is not (LodestoneOutcome.Found or LodestoneOutcome.NotFound))
        {
            return ActionAnswer.Fail(StatusCodes.Status503ServiceUnavailable, "reread:" + read.Outcome);
        }

        var applied = await bindings.ApplyRereadAndReadAsync(persona, binding.LodestoneId, read.Character, cancellation);

        // A takeover that landed while the read was under way, through a pipe or the server's own
        // client, answers as one found before it did: "taken over", never "not bound", which a plugin
        // follows with an opt-out that would forget the takeover.
        if (applied.Result is RereadResult.NotBound && await bindings.WasTakenOverAsync(persona, cancellation))
        {
            return ActionAnswer.Fail(StatusCodes.Status410Gone, "reread:taken-over");
        }

        if (applied.Result is RereadResult.Removed or RereadResult.NotBound)
        {
            // A character with no binding any more stops counting as online at once, instead of
            // being counted while its heartbeats keep a session the start's binding check passed.
            presence.ForgetKey(persona);
            return ActionAnswer.Fail(StatusCodes.Status404NotFound, "reread:" + applied.Result);
        }

        // The name, World and day come from the transaction that applied the read, so they are this
        // binding's, whatever happened since; a POST's body never carries the day.
        return ActionAnswer.Ok(new CharacterEndpoints.RereadAnswer(applied.Name!, applied.World!)) with { ReadDay = applied.ReadDay };
    }

    /// <summary>
    /// What a check answers at once when the read through a pipe failed before any page: "try again
    /// later" (<c>503</c>) when no place was free or no complete answer came, and a <c>503</c> with
    /// <see cref="RefusedReason"/> when the Lodestone refused the player's connection. Null otherwise.
    /// </summary>
    private static ActionAnswer? PipeFailure(LodestoneRead read, string prefix) => read.Outcome switch
    {
        LodestoneOutcome.Busy => ActionAnswer.Fail(StatusCodes.Status503ServiceUnavailable, prefix + "pipe-busy"),
        LodestoneOutcome.Unanswered => ActionAnswer.Fail(StatusCodes.Status503ServiceUnavailable, prefix + "unanswered"),
        LodestoneOutcome.Refused => new ActionAnswer(StatusCodes.Status503ServiceUnavailable, prefix + "refused", Reason: RefusedReason),
        _ => null,
    };

    /// <summary>C6's "10 a day per Lodestone id and address range", counted for each of the address's groups.</summary>
    private bool TakeCheckForId(long lodestoneId, IPAddress? address)
    {
        foreach (var (group, multiple) in AddressGroups.Of(address))
        {
            if (!limiter.TryTake(ServerLimits.ChecksPerLodestoneId, lodestoneId + "|" + group, multiple))
            {
                return false;
            }
        }

        return true;
    }
}
