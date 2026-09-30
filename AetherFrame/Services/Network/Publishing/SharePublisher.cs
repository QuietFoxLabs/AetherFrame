using System;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>What the window reads of the publisher each frame: one immutable value, replaced whole.</summary>
internal sealed class SharePublisherView
{
    internal static readonly SharePublisherView Empty = new(false, null, default, null);

    internal SharePublisherView(bool busy, PublishOutcome? lastOutcome, PersonaSlotId listed, LoadedPublications? publications)
    {
        Busy = busy;
        LastOutcome = lastOutcome;
        Listed = listed;
        Publications = publications;
    }

    /// <summary>Whether a signing or a load was handed to the persona session and hasn't ended.</summary>
    internal bool Busy { get; }

    /// <summary>What the latest signing came to.</summary>
    internal PublishOutcome? LastOutcome { get; }

    /// <summary>The persona whose publications <see cref="Publications"/> lists.</summary>
    internal PersonaSlotId Listed { get; }

    /// <summary>That persona's publications, as the latest load found them.</summary>
    internal LoadedPublications? Publications { get; }
}

/// <summary>
/// Signing a candidate into a persona's outbox, and listing what the persona published, as
/// persona-session operations (N2-6c's second part): one at a time, under the persona files' lock,
/// off the framework thread, and waited for by unloading. The session runs the work
/// (<see cref="SharePublisher(Func{string, Action{PersonaManager}, bool}, PublicationFiles, Func{DateTimeOffset}, Action{string})"/>'s
/// <c>tryRun</c>, the persona session's <c>TryRun</c>), which keeps the persona window's own
/// outcomes apart. Nothing here sends anything: N2-9 does. The log gets result kinds and exception
/// kinds only, never an id, a path or a Plate's text. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class SharePublisher
{
    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly PublicationFiles files;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string> log;
    private readonly object gate = new();
    private volatile SharePublisherView view = SharePublisherView.Empty;

    /// <summary>A publisher that runs its work through <paramref name="tryRun"/> (false when nothing started), in <paramref name="files"/>, at <paramref name="utcNow"/>'s time.</summary>
    internal SharePublisher(Func<string, Action<PersonaManager>, bool> tryRun, PublicationFiles files, Func<DateTimeOffset> utcNow, Action<string> log)
    {
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Where signing and listing stand.</summary>
    internal SharePublisherView View => view;

    /// <summary>
    /// Signs <paramref name="candidate"/> as the persona shown (<paramref name="shownSlot"/> with
    /// <paramref name="shownKey"/>, L10) into its outbox, then lists its publications again; false
    /// when the session couldn't start it (not ready, or busy), and then nothing ran.
    /// </summary>
    internal bool TrySign(SnapshotCandidate candidate, PersonaSlotId shownSlot, PersonaPublicKey shownKey)
    {
        var consent = new PublishConsent(candidate, shownSlot, shownKey);
        return Run("publish", manager =>
        {
            var outcome = PublicationCommit.Commit(manager, files, consent, utcNow);
            log(outcome.Failure is { } failure ? $"Sharing: signing came to {outcome.Result}: {failure}" : $"Sharing: signing came to {outcome.Result}.");

            // The list is loaded again in the same operation, so it shows what the saved index says.
            var loaded = PublicationLoad.Load(files, shownSlot, shownKey);
            LogLoad(loaded);
            return new SharePublisherView(false, outcome, shownSlot, loaded);
        });
    }

    /// <summary>Lists <paramref name="slot"/>'s publications, checking its outbox against <paramref name="key"/>; false when the session couldn't start it.</summary>
    internal bool TryList(PersonaSlotId slot, PersonaPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Run("list publications", _ =>
        {
            var loaded = PublicationLoad.Load(files, slot, key);
            LogLoad(loaded);
            var current = view;
            return new SharePublisherView(false, current.LastOutcome, slot, loaded);
        });
    }

    /// <summary>Forgets the latest signing's outcome, when the window checks another Plate.</summary>
    internal void ClearOutcome()
    {
        lock (gate)
        {
            var current = view;
            view = new SharePublisherView(current.Busy, null, current.Listed, current.Publications);
        }
    }

    private bool Run(string name, Func<PersonaManager, SharePublisherView> work)
    {
        lock (gate)
        {
            if (view.Busy)
            {
                return false;
            }

            var current = view;
            view = new SharePublisherView(true, current.LastOutcome, current.Listed, current.Publications);
        }

        bool started;
        try
        {
            started = tryRun(name, manager =>
            {
                SharePublisherView? next = null;
                try
                {
                    next = work(manager);
                }
                finally
                {
                    lock (gate)
                    {
                        var current = view;
                        view = next ?? new SharePublisherView(false, current.LastOutcome, current.Listed, current.Publications);
                    }
                }
            });
        }
        catch
        {
            started = false;
        }

        if (!started)
        {
            lock (gate)
            {
                var current = view;
                view = new SharePublisherView(false, current.LastOutcome, current.Listed, current.Publications);
            }
        }

        return started;
    }

    private void LogLoad(LoadedPublications loaded)
    {
        if (loaded.Result != PublicationLoadResult.Loaded)
        {
            log($"Sharing: a persona's publications came to {loaded.Result}: {loaded.Failure}");
        }
        else if (loaded.Removed > 0)
        {
            log($"Sharing: {loaded.Removed} outbox file(s) no index names were removed.");
        }
    }
}
