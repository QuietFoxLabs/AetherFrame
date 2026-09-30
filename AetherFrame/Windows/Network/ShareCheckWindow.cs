using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Personas;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Personas;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The preview's share check (N2-6c's second part), reached from a Plate's menu in My Plates: what
/// sharing the Plate would send, from a private copy of its saved state, or why it can't be shared;
/// signing it as the persona in use, kept on this PC; and what that persona has signed from here.
/// Nothing is sent: that is N2-9, whose consent screen this is not. Every text from the Plate or the
/// player (the Plate's name, its texts, element names, the persona's label) is drawn unformatted,
/// never inside an ImGui label, a tooltip or a format string (N7, D9a). Compiled only in the
/// networking preview flavour.
/// </summary>
internal sealed class ShareCheckWindow : Window, IDisposable
{
    private const string Intro =
        "What sharing this Plate would send, checked from its saved state, or why it can't be shared yet. " +
        "Signing keeps the signed Plate on this PC. Nothing is sent: sharing with others comes in a later preview.";

    private const string NothingSent = "Nothing is sent: it waits on this PC until sharing arrives in a later preview.";

    private readonly ShareCheck check;
    private readonly SharePublisher publisher;
    private readonly PersonaSession session;
    private readonly ITextureProvider textures;
    private readonly Func<Guid, string?> plateName;
    private readonly AetherWindowChrome chrome = new();
    private Thumbnails? thumbnails;
    private SnapshotCandidate? signed;
    private PersonaSlotId listRequested;

    internal ShareCheckWindow(ShareCheck check, SharePublisher publisher, PersonaSession session, ITextureProvider textures, Func<Guid, string?> plateName)
        : base("Check what would be shared (preview)##AetherFrameShareCheck", ImGuiWindowFlags.NoCollapse)
    {
        this.check = check ?? throw new ArgumentNullException(nameof(check));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.plateName = plateName ?? throw new ArgumentNullException(nameof(plateName));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480f, 360f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;
    }

    /// <summary>Opens the window on <paramref name="plateId"/>'s saved state, checking it afresh.</summary>
    internal void Open(Guid plateId)
    {
        ReleaseThumbnails();
        publisher.ClearOutcome();
        signed = null;
        check.Begin(plateId);
        IsOpen = true;
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void OnClose()
    {
        check.Reset();
        ReleaseThumbnails();
        signed = null;
    }

    public override void Draw()
    {
        check.OnFrame();
        var view = check.View;
        var active = session.Active;
        var sessionView = session.View;

        Wrapped(Intro, AetherPalette.TextMuted);
        AetherControls.Divider();

        AetherControls.Secondary("Plate");
        Wrapped(view.PlateName.Length == 0 ? "(no name)" : view.PlateName);
        DrawPersona(active, sessionView);
        AetherControls.Divider();

        switch (view.Stage)
        {
            case ShareCheckStage.Resolving:
                AetherControls.StatusLine(AetherTone.Info, "Checking what it draws...");
                break;
            case ShareCheckStage.Preparing:
                AetherControls.StatusLine(AetherTone.Info, "Preparing its images...");
                break;
            case ShareCheckStage.Failed:
                AetherControls.Callout(AetherTone.Warning, ShareMessages.For(view.Failure));
                DrawCheckAgain(view);
                break;
            case ShareCheckStage.Refused:
                DrawRefused(view);
                DrawCheckAgain(view);
                break;
            case ShareCheckStage.Ready when view.Candidate is { } candidate:
                DrawCandidate(candidate);
                DrawSigning(view, candidate, active, sessionView);
                break;
        }

        DrawPublications(active, sessionView);
    }

    public void Dispose()
    {
        check.Dispose();
        ReleaseThumbnails();
    }

    /// <summary>Draws text wrapped to the window, unformatted: safe for anything the player wrote.</summary>
    private static void Wrapped(string text, Vector4? color = null)
    {
        if (color is { } tint)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
        }

        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        if (color is not null)
        {
            ImGui.PopStyleColor();
        }
    }

    private static string Describe(ProfileElement? element, PlateComponent? component, bool background)
    {
        if (background)
        {
            return "The background";
        }

        if (component is not null)
        {
            return "A component";
        }

        return element switch
        {
            TextProfileElement => "A text",
            ImageProfileElement => "An image",
            null => "The Plate",
            _ => "An element",
        };
    }

    private static string ByteSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB";

    private void DrawPersona(PersonaRecord? active, PersonaSessionView sessionView)
    {
        AetherControls.Secondary("Signs as");
        if (sessionView.State != PersonaSessionState.Ready)
        {
            AetherControls.StatusLine(AetherTone.Warning, "Personas aren't ready. The Personas window says why.");
            return;
        }

        if (active is null)
        {
            AetherControls.StatusLine(AetherTone.Warning, "No persona is in use. Choose one in the Personas window to sign.");
            return;
        }

        Wrapped(active.Label);
        Wrapped(active.Id.ToString(), AetherPalette.TextMuted);
        if (!active.Acknowledged)
        {
            AetherControls.StatusLine(AetherTone.Warning, "First acknowledge, in the Personas window, what losing this persona's key means.");
        }
    }

    private void DrawRefused(ShareCheckView view)
    {
        AetherControls.Callout(AetherTone.Danger, "This Plate can't be shared as it is. Change what is listed here, save it, and check again.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var problem in view.Problems)
        {
            var subject = Describe(problem.Element, problem.Component, problem.Background);
            var message = ShareMessages.For(problem.Refusal);
            if (!seen.Add(subject + "\n" + message + "\n" + problem.Element?.Name))
            {
                continue;
            }

            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(problem.Element is { Name.Length: > 0 } named ? subject + " (" + named.Name + "): " + message : subject + ": " + message);
        }
    }

    private void DrawCheckAgain(ShareCheckView view)
    {
        if (view.PlateId != Guid.Empty && AetherControls.SecondaryButton("Check again"))
        {
            Open(view.PlateId);
        }
    }

    private void DrawCandidate(SnapshotCandidate candidate)
    {
        if (!ReferenceEquals(thumbnails?.Candidate, candidate))
        {
            ReleaseThumbnails();
            thumbnails = new Thumbnails(textures, candidate);
        }

        AetherControls.SectionHeader("Its texts, shared in full");
        var any = false;
        for (var index = 0; index < candidate.Items.Count; index++)
        {
            if (candidate.Items[index] is not LayoutText text)
            {
                continue;
            }

            any = true;
            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(text.Text.Length == 0 ? "(an empty text)" : text.Text);
            if (ShareMessages.IsFromTheCharacter(candidate.Roles[index]))
            {
                Wrapped("Filled in from your character.", AetherPalette.TextMuted);
            }
        }

        if (!any)
        {
            AetherControls.Muted("No texts.");
        }

        AetherControls.SectionHeader("Its images, as they would be shared");
        if (candidate.Images.Count == 0)
        {
            AetherControls.Muted("No images.");
        }

        var side = 128f * ImGuiHelpers.GlobalScale;
        for (var index = 0; index < candidate.Images.Count; index++)
        {
            var image = candidate.Images[index];
            var scale = Math.Min(side / image.Width, side / image.Height);
            var size = new Vector2(image.Width * scale, image.Height * scale);
            if (thumbnails?[index] is { } wrap)
            {
                ImGui.Image(wrap.Handle, size);
            }
            else
            {
                ImGui.Dummy(size);
            }

            ImGui.SameLine();
            AetherControls.Muted(string.Create(CultureInfo.InvariantCulture, $"{image.Width} x {image.Height}, {(image.Format == ImageFormat.Jpeg ? "JPEG" : "PNG")}, {ByteSize(image.ByteLength)}"));
        }

        if (candidate.LeftOut.Count > 0)
        {
            AetherControls.SectionHeader("Left out, since it isn't drawn");
            foreach (var left in candidate.LeftOut)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                var subject = Describe(left.Element, left.Component, false);
                Wrapped(left.Element is { Name.Length: > 0 } named
                    ? subject + " (" + named.Name + ") " + ShareMessages.For(left.Reason)
                    : subject + " " + ShareMessages.For(left.Reason));
            }
        }

        AetherControls.Divider();
    }

    private void DrawSigning(ShareCheckView view, SnapshotCandidate candidate, PersonaRecord? active, PersonaSessionView sessionView)
    {
        var used = ReferenceEquals(signed, candidate);
        var publisherView = publisher.View;
        var canSign = !used && active is { Acknowledged: true } && sessionView.State == PersonaSessionState.Ready && !sessionView.Busy && !publisherView.Busy;
        using (ImRaii.Disabled(!canSign))
        {
            if (AetherControls.PrimaryButton("Sign and keep on this PC") && canSign && active is { } persona && publisher.TrySign(candidate, persona.Slot, persona.PublicKey))
            {
                // One signing attempt a check (SnapshotCandidate.TryClaimForSigning): another needs a new check.
                signed = candidate;
            }
        }

        ImGui.SameLine();
        DrawCheckAgain(view);

        if (publisherView.Busy && used)
        {
            AetherControls.StatusLine(AetherTone.Info, "Signing...");
        }
        else if (publisherView.LastOutcome is { } outcome && used)
        {
            var stored = outcome.Result == PublishResult.Stored;
            AetherControls.StatusLine(stored ? AetherTone.Success : AetherTone.Warning, ShareMessages.For(outcome.Result));
            if (stored)
            {
                AetherControls.Muted(NothingSent);
            }
        }
    }

    private void DrawPublications(PersonaRecord? active, PersonaSessionView sessionView)
    {
        if (active is null || sessionView.State != PersonaSessionState.Ready)
        {
            return;
        }

        AetherControls.Divider();
        AetherControls.SectionHeader("Signed from this PC by this persona");
        var publisherView = publisher.View;
        if (publisherView.Listed != active.Slot || publisherView.Publications is null)
        {
            // Asked for once per persona shown; a busy session is asked again next frame.
            if (listRequested != active.Slot && publisher.TryList(active.Slot, active.PublicKey))
            {
                listRequested = active.Slot;
            }

            AetherControls.StatusLine(AetherTone.Info, "Reading its list...");
            return;
        }

        var loaded = publisherView.Publications;
        if (loaded.Result != PublicationLoadResult.Loaded)
        {
            AetherControls.Callout(AetherTone.Warning, ShareMessages.For(loaded.Result));
            return;
        }

        if (loaded.Entries.Count == 0)
        {
            AetherControls.Muted("Nothing yet.");
            return;
        }

        foreach (var entry in loaded.Entries)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(plateName(entry.Entry.PlateId) is { Length: > 0 } name ? name : "(a Plate that no longer exists)");
            ImGui.SameLine();
            AetherControls.MutedInline(ShareMessages.For(entry.Entry.State, entry.Outbox));
        }
    }

    private void ReleaseThumbnails()
    {
        thumbnails?.Dispose();
        thumbnails = null;
    }

    /// <summary>The prepared copies of a candidate, decoded for display; each is drawn once it has loaded, and none after <see cref="Dispose"/>.</summary>
    private sealed class Thumbnails : IDisposable
    {
        private readonly IDalamudTextureWrap?[] wraps;
        private bool disposed;

        internal Thumbnails(ITextureProvider textures, SnapshotCandidate candidate)
        {
            Candidate = candidate;
            wraps = new IDalamudTextureWrap?[candidate.ImageBytes.Count];
            for (var index = 0; index < wraps.Length; index++)
            {
                _ = LoadAsync(textures, candidate.ImageBytes[index], index);
            }
        }

        internal SnapshotCandidate Candidate { get; }

        internal IDalamudTextureWrap? this[int index]
        {
            get
            {
                lock (wraps)
                {
                    return index < wraps.Length ? wraps[index] : null;
                }
            }
        }

        public void Dispose()
        {
            lock (wraps)
            {
                disposed = true;
                for (var index = 0; index < wraps.Length; index++)
                {
                    wraps[index]?.Dispose();
                    wraps[index] = null;
                }
            }
        }

        private async Task LoadAsync(ITextureProvider textures, ReadOnlyMemory<byte> bytes, int index)
        {
            try
            {
                var wrap = await textures.CreateFromImageAsync(bytes, "AetherFrame share check").ConfigureAwait(false);
                lock (wraps)
                {
                    if (disposed)
                    {
                        wrap.Dispose();
                        return;
                    }

                    wraps[index] = wrap;
                }
            }
            catch (Exception)
            {
                // The copy was checked when it was prepared; a thumbnail that doesn't load is left blank.
            }
        }
    }
}
