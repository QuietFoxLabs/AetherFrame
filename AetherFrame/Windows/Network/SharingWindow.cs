using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Services.Network.Personas;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The sharing window (NETWORK2's N2-9b; decision batch C, C1 to C4): for the logged-in character,
/// the consent to turn sharing on, the Lodestone code and check, and turning sharing off. It reads
/// the service's view each frame, which never waits, and hands every change to the service, which
/// runs it off the framework thread. A name, a World or a code is only ever drawn unformatted,
/// never inside an ImGui label, a tooltip or a format string. The consent's tick is cleared
/// whenever the consent isn't on screen, so it is always given afresh. Compiled only in the
/// networking preview flavour.
/// </summary>
internal sealed class SharingWindow : Window
{
    private const int AddressBufferBytes = 256;

    private readonly CharacterSharing sharing;
    private readonly PersonaSession session;
    private readonly Func<CharacterContext?> currentCharacter;
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

    internal SharingWindow(CharacterSharing sharing, PersonaSession session, Func<CharacterContext?> currentCharacter, string sharingFile)
        : base("Sharing##AetherFrameSharing", ImGuiWindowFlags.NoCollapse)
    {
        this.sharing = sharing;
        this.session = session;
        this.currentCharacter = currentCharacter;
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
    }

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
            AetherControls.Callout(SharingText.IsProblem(notice.Kind) ? AetherTone.Warning : AetherTone.Success, SharingText.Notice(notice.Kind));
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

        AetherControls.SectionHeader("Sharing is on");
        Wrapped(SharingText.SharedLine);
        ImGui.Indent();
        ImGui.TextUnformatted(entry.Name ?? "");
        ImGui.TextUnformatted(entry.World ?? "");
        ImGui.Unindent();
        ImGui.Spacing();
        Wrapped(SharingText.PublishingLater, AetherPalette.TextMuted);
        ImGui.Spacing();

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
