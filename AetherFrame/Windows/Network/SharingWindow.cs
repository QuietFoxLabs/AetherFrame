using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Network.Personas;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The sharing window (NETWORK2's N2-9b and N2-9c; decision batch C, C1 to C4): for the logged-in
/// character, the consent to turn sharing on, the Lodestone code and check, the first showing of a
/// Plate before it is shared (C3), pausing, resuming and turning sharing off. It reads
/// the service's view each frame, which never waits, and hands every change to the service, which
/// runs it off the framework thread. A name, a World or a code is only ever drawn unformatted,
/// never inside an ImGui label, a tooltip or a format string. The consent's tick is cleared
/// whenever the consent isn't on screen, so it is always given afresh. Compiled only in the
/// networking preview flavour.
/// </summary>
internal sealed class SharingWindow : Window, IDisposable
{
    private const int AddressBufferBytes = 256;

    private readonly CharacterSharing sharing;
    private readonly LivePublisher live;
    private readonly CandidateView candidateView;
    private readonly PersonaSession session;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly Action<ProfileDocument> viewDocument;
    private readonly Func<ulong, Guid?> activePlateOf;
    private readonly string sharingFile;
    private readonly string applicationData;
    private readonly string userProfile;
    private readonly AetherWindowChrome chrome = new();
    private readonly HashSet<ulong> rereadAsked = new();
    private bool agreed;
    private bool consentShown;
    private ulong shownCharacter;
    private string address = "";
    private bool addressInvalid;
    private ulong confirmingOff;
    private bool confirmingAll;

    internal SharingWindow(CharacterSharing sharing, LivePublisher live, ITextureProvider textures, PersonaSession session, Func<CharacterContext?> currentCharacter, Func<ulong, Guid?> activePlateOf, Action<ProfileDocument> viewDocument, string sharingFile)
        : base("Sharing##AetherFrameSharing", ImGuiWindowFlags.NoCollapse)
    {
        this.sharing = sharing;
        this.live = live;
        candidateView = new CandidateView(textures);
        this.session = session;
        this.currentCharacter = currentCharacter;
        this.viewDocument = viewDocument;
        this.activePlateOf = activePlateOf;
        this.sharingFile = sharingFile;
        applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480f, 360f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void OnClose()
    {
        agreed = false;
        confirmingOff = 0;
        confirmingAll = false;
        candidateView.Release();
    }

    public void Dispose() => candidateView.Dispose();

    public override void Draw()
    {
        consentShown = false;
        DrawContent();

        // The tick means "I agree to what is on screen now": it never outlives the screen.
        if (!consentShown)
        {
            agreed = false;
        }
    }

    private static void Wrapped(string text, Vector4? color = null)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawContent()
    {
        Wrapped(SharingText.Intro, AetherPalette.TextMuted);
        AetherControls.Divider();

        var personas = session.View;
        if (personas.State == PersonaSessionState.Starting)
        {
            AetherControls.StatusLine(AetherTone.Info, SharingText.Starting);
            return;
        }

        if (personas.State != PersonaSessionState.Ready)
        {
            AetherControls.Callout(AetherTone.Warning, SharingText.PersonasUnavailable + " " + (personas.Message ?? ""));
            return;
        }

        var view = sharing.View;
        if (!view.Loaded)
        {
            sharing.TryLoad();
            AetherControls.StatusLine(AetherTone.Info, SharingText.Starting);
            return;
        }

        if (view.Unreadable)
        {
            AetherControls.Callout(AetherTone.Danger, SharingText.Unreadable);
            Wrapped(PersonaWindowModel.DisplayPath(System.IO.Path.GetDirectoryName(sharingFile) ?? sharingFile, applicationData, userProfile), AetherPalette.TextMuted);
            if (!view.Busy && AetherControls.SecondaryButton("Try again"))
            {
                sharing.TryLoad();
            }

            return;
        }

        var character = currentCharacter();
        if ((character?.ContentId ?? 0) != shownCharacter)
        {
            shownCharacter = character?.ContentId ?? 0;
            address = "";
            addressInvalid = false;
            confirmingOff = 0;
        }

        if (view.Notice is { } notice && (notice.ContentId == 0 || notice.ContentId == character?.ContentId))
        {
            AetherControls.Callout(SharingText.IsProblem(notice.Kind) ? AetherTone.Warning : AetherTone.Success, SharingText.Notice(notice));
            ImGui.Spacing();
        }

        if (view.Busy)
        {
            AetherControls.StatusLine(AetherTone.Info, SharingText.Busy);
        }

        if (character is not { } current)
        {
            AetherControls.Muted(SharingText.NoCharacter);
        }
        else
        {
            var entry = view.Find(current.ContentId);
            var keyLost = view.Notice is { Kind: SharingNoticeKind.KeyUnavailable } lost && lost.ContentId == current.ContentId;
            switch (keyLost ? null : entry)
            {
                case { Checking: true } checking:
                    DrawCheck(view, checking, current);
                    break;
                case { IsBound: true } bound:
                    DrawShared(view, bound, current);
                    break;
                default:
                    // A key that can't be opened is replaced by a new one: a new check moves the
                    // character to it (C1), and until then its binding stays as it is.
                    DrawConsent(view, current, newKey: keyLost);
                    break;
            }
        }

        DrawTurnOffAll(view);
    }

    private void DrawConsent(CharacterSharingView view, CharacterContext character, bool newKey)
    {
        consentShown = true;
        AetherControls.SectionHeader(newKey ? SharingText.NewKeyTitle : SharingText.ConsentTitle);
        foreach (var statement in SharingText.Consent)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(statement);
        }

        ImGui.Spacing();
        ImGui.Checkbox(SharingText.Agree, ref agreed);
        using (ImRaii.Disabled(!agreed || view.Busy))
        {
            if (AetherControls.PrimaryButton(newKey ? SharingText.NewKeyTitle : SharingText.TurnOn) && agreed)
            {
                agreed = false;
                address = "";
                addressInvalid = false;
                sharing.TryStart(character.ContentId, newKey);
            }
        }
    }

    private void DrawCheck(CharacterSharingView view, SharingCharacter entry, CharacterContext character)
    {
        AetherControls.SectionHeader("Prove this character is yours");
        if (entry.ReplacingKey)
        {
            AetherControls.Callout(AetherTone.Info, SharingText.NewKeyBound);
        }

        if (view.Code is not { } code || code.ContentId != character.ContentId || code.Slot != entry.CheckingSlot)
        {
            AetherControls.Muted(SharingText.NoCodeYet);
            using (ImRaii.Disabled(view.Busy))
            {
                if (AetherControls.PrimaryButton("Get a code"))
                {
                    sharing.TryNewCode(entry.ContentId);
                }
            }

            DrawCancel(view, entry);
            return;
        }

        Wrapped(SharingText.CodeStepCopy);
        using (AetherFonts.Heading())
        {
            ImGui.TextUnformatted(code.Code);
        }

        ImGui.SameLine();
        if (AetherControls.SecondaryButton("Copy"))
        {
            ImGui.SetClipboardText(code.Code);
        }

        Wrapped(SharingText.CodeWarning, AetherPalette.Warning);
        AetherControls.Muted(SharingText.CodeLeft(code.Expires - DateTimeOffset.UtcNow));
        ImGui.Spacing();
        Wrapped(SharingText.CodeStepPaste);
        ImGui.Spacing();
        Wrapped(SharingText.CodeStepAddress);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##lodestoneAddress", SharingText.AddressHint, ref address, AddressBufferBytes))
        {
            addressInvalid = false;
        }

        if (addressInvalid)
        {
            Wrapped(SharingText.AddressInvalid, AetherPalette.Warning);
        }

        var named = character.Name is { Length: > 0 } && character.HomeWorld is { Length: > 0 };
        if (!named)
        {
            Wrapped(SharingText.NoNameYet, AetherPalette.Warning);
        }

        using (ImRaii.Disabled(view.Busy || address.Trim().Length == 0 || !named))
        {
            if (AetherControls.PrimaryButton("Check") && character.Name is { } name && character.HomeWorld is { } world)
            {
                if (LodestoneAddress.TryReadId(address, out var lodestoneId))
                {
                    sharing.TryCheck(entry.ContentId, lodestoneId, name, world);
                }
                else
                {
                    addressInvalid = true;
                }
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(view.Busy))
        {
            if (AetherControls.SecondaryButton("Get a new code"))
            {
                sharing.TryNewCode(entry.ContentId);
            }
        }

        AetherControls.Muted(SharingText.CodeLife);
        DrawCancel(view, entry);
    }

    private void DrawCancel(CharacterSharingView view, SharingCharacter entry)
    {
        ImGui.Spacing();
        using (ImRaii.Disabled(view.Busy))
        {
            if (AetherControls.GhostButton("Cancel"))
            {
                sharing.TryCancelCheck(entry.ContentId);
            }
        }
    }

    private void DrawShared(CharacterSharingView view, SharingCharacter entry, CharacterContext character)
    {
        // C1: the binding follows a rename or a World transfer once the server reads the page
        // again, which it is asked to do at most once a session for each character, when the game
        // shows another name or World.
        if (!view.Busy && character.Name is { } name && character.HomeWorld is { } world && !CharacterSharing.SameCharacter(entry, name, world)
            && rereadAsked.Add(entry.ContentId) && !sharing.TryReread(entry.ContentId, name, world))
        {
            rereadAsked.Remove(entry.ContentId);
        }

        var paused = entry.Stage == SharingStage.Paused;
        AetherControls.SectionHeader(paused ? "Sharing is paused" : "Sharing is on");
        Wrapped(SharingText.SharedLine);
        ImGui.Indent();
        ImGui.TextUnformatted(entry.Name ?? "");
        ImGui.TextUnformatted(entry.World ?? "");
        ImGui.Unindent();
        ImGui.Spacing();
        Wrapped(paused ? SharingText.PausedLine : SharingText.SavingShares, AetherPalette.TextMuted);
        ImGui.Spacing();

        if (!paused)
        {
            DrawPublishing(view, entry);
        }

        using (ImRaii.Disabled(view.Busy))
        {
            if (paused ? AetherControls.PrimaryButton("Resume sharing") : AetherControls.SecondaryButton("Pause sharing"))
            {
                if (paused)
                {
                    sharing.TryResume(entry.ContentId);
                }
                else
                {
                    sharing.TryPause(entry.ContentId);
                }
            }
        }

        ImGui.SameLine();

        if (confirmingOff == entry.ContentId)
        {
            AetherControls.Callout(AetherTone.Warning, SharingText.TurnOffConfirm);
            using (ImRaii.Disabled(view.Busy))
            {
                if (AetherControls.DangerButton("Turn off sharing"))
                {
                    confirmingOff = 0;
                    sharing.TryTurnOff(entry.ContentId);
                }
            }

            ImGui.SameLine();
            if (AetherControls.SecondaryButton("Keep sharing"))
            {
                confirmingOff = 0;
            }

            return;
        }

        using (ImRaii.Disabled(view.Busy))
        {
            if (AetherControls.SecondaryButton("Turn off sharing for this character..."))
            {
                confirmingOff = entry.ContentId;
            }
        }
    }

    /// <summary>The Active Plate's way out: being prepared, can't be shared as it is, waiting to be sent, or shown before its first send (C3).</summary>
    private void DrawPublishing(CharacterSharingView view, SharingCharacter entry)
    {
        var liveView = live.View;
        if (liveView.ContentId == entry.ContentId)
        {
            if (liveView.Building)
            {
                AetherControls.StatusLine(AetherTone.Info, SharingText.Building);
            }
            else if (liveView.Problems.Count > 0)
            {
                AetherControls.Callout(AetherTone.Warning, SharingText.CantShare);
                CandidateView.DrawProblems(liveView.Problems);
            }
            else if (liveView.Failure != Services.Network.Publishing.ShareCheckFailure.None)
            {
                AetherControls.Callout(AetherTone.Warning, Services.Network.Publishing.ShareMessages.For(liveView.Failure));
            }
        }

        if (view.Busy && sharing.Uploading)
        {
            AetherControls.StatusLine(AetherTone.Info, SharingText.Sending);
            if (AetherControls.SecondaryButton("Stop sending"))
            {
                sharing.StopSending();
            }
        }

        if (view.Notice is { Kind: SharingNoticeKind.PublishWaiting } waiting && waiting.ContentId == entry.ContentId)
        {
            using (ImRaii.Disabled(view.Busy))
            {
                if (AetherControls.SecondaryButton("Try sending again"))
                {
                    sharing.TrySendWaiting(entry.ContentId);
                }
            }
        }

        // Never beside a newer build of the Active Plate, whatever it came to: only its own showing.
        var rebuilding = liveView.ContentId == entry.ContentId
            && (liveView.Building || liveView.Problems.Count > 0 || liveView.Failure != Services.Network.Publishing.ShareCheckFailure.None);
        if (view.Consent is not { } consent || consent.ContentId != entry.ContentId || rebuilding)
        {
            candidateView.Release();
            return;
        }

        // C3's first showing: exactly what will be signed and sent, and nothing until the player
        // agrees. Only a candidate for the Active Plate now can be shared from here.
        var active = activePlateOf(entry.ContentId);
        AetherControls.Divider();
        AetherControls.SectionHeader(SharingText.FirstShowingTitle);
        Wrapped(SharingText.FirstShowing);
        if (consent.Source is { } source && AetherControls.SecondaryButton("View it as drawn"))
        {
            viewDocument(source);
        }

        var images = candidateView.Draw(consent.Candidate);
        if (images != CandidateImages.Shown)
        {
            AetherControls.Muted(SharingText.FirstShowingImages);
        }

        var current = consent.Candidate.PlateId == active;
        using (ImRaii.Disabled(view.Busy || images != CandidateImages.Shown || !current))
        {
            if (AetherControls.PrimaryButton("Share this Plate") && images == CandidateImages.Shown && current)
            {
                sharing.TryPublish(entry.ContentId, consent.Candidate, approved: true, active);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(view.Busy))
        {
            if (AetherControls.SecondaryButton("Not now"))
            {
                sharing.DeclineConsent(entry.ContentId);
            }
        }

        AetherControls.Divider();
    }

    private void DrawTurnOffAll(CharacterSharingView view)
    {
        var any = false;
        foreach (var character in view.Characters)
        {
            any |= character.IsBound || character.Stage == SharingStage.Checking;
        }

        if (!any)
        {
            return;
        }

        AetherControls.Divider();
        if (confirmingAll)
        {
            AetherControls.Callout(AetherTone.Warning, SharingText.TurnOffAllConfirm);
            using (ImRaii.Disabled(view.Busy))
            {
                if (AetherControls.DangerButton("Turn off sharing everywhere"))
                {
                    confirmingAll = false;
                    sharing.TryTurnOffAll();
                }
            }

            ImGui.SameLine();
            if (AetherControls.SecondaryButton("Keep sharing##all"))
            {
                confirmingAll = false;
            }

            return;
        }

        using (ImRaii.Disabled(view.Busy))
        {
            if (AetherControls.GhostButton("Turn off sharing for every character..."))
            {
                confirmingAll = true;
            }
        }
    }
}
