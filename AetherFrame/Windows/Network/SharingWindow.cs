using System;
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
/// never inside an ImGui label, a tooltip or a format string. Compiled only in the networking
/// preview flavour.
/// </summary>
internal sealed class SharingWindow : Window
{
    private const int AddressBufferBytes = 256;

    private readonly CharacterSharing sharing;
    private readonly PersonaSession session;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly AetherWindowChrome chrome = new();
    private ulong agreedFor;
    private bool agreed;
    private string address = "";
    private bool addressInvalid;
    private ulong confirmingOff;
    private bool confirmingAll;
    private ulong rereadAskedFor;

    internal SharingWindow(CharacterSharing sharing, PersonaSession session, Func<CharacterContext?> currentCharacter)
        : base("Sharing##AetherFrameSharing", ImGuiWindowFlags.NoCollapse)
    {
        this.sharing = sharing;
        this.session = session;
        this.currentCharacter = currentCharacter;
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

    public override void Draw()
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
            if (!view.Busy && AetherControls.SecondaryButton("Try again"))
            {
                sharing.TryLoad();
            }

            return;
        }

        var character = currentCharacter();
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
                case { Stage: SharingStage.Checking } checking:
                    DrawCheck(view, checking, current);
                    break;
                case { IsBound: true } bound:
                    DrawShared(view, bound, current);
                    break;
                default:
                    // A key that can't be opened is replaced by a new one: a new check moves the
                    // character to it (C1).
                    DrawConsent(view, current, newKey: keyLost);
                    break;
            }
        }

        DrawTurnOffAll(view);
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

    private void DrawConsent(CharacterSharingView view, CharacterContext character, bool newKey)
    {
        if (agreedFor != character.ContentId)
        {
            agreedFor = character.ContentId;
            agreed = false;
        }

        AetherControls.SectionHeader(SharingText.ConsentTitle);
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
            if (AetherControls.PrimaryButton(SharingText.TurnOn) && agreed)
            {
                address = "";
                addressInvalid = false;
                sharing.TryStart(character.ContentId, newKey);
            }
        }
    }

    private void DrawCheck(CharacterSharingView view, SharingCharacter entry, CharacterContext character)
    {
        AetherControls.SectionHeader("Prove this character is yours");
        if (view.Code is not { } code || code.ContentId != character.ContentId)
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

        using (ImRaii.Disabled(view.Busy || address.Trim().Length == 0))
        {
            if (AetherControls.PrimaryButton("Check"))
            {
                if (LodestoneAddress.TryReadId(address, out var lodestoneId))
                {
                    sharing.TryCheck(entry.ContentId, lodestoneId);
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
                sharing.TryTurnOff(entry.ContentId);
            }
        }
    }

    private void DrawShared(CharacterSharingView view, SharingCharacter entry, CharacterContext character)
    {
        // C1: the binding follows a rename or a World transfer once the server reads the page
        // again, which it is asked to do once per session when the game shows another name or World.
        if (!view.Busy && rereadAskedFor != entry.ContentId && character.Name is { } name && character.HomeWorld is { } world
            && !CharacterSharing.SameCharacter(entry, name, world))
        {
            rereadAskedFor = entry.ContentId;
            sharing.TryReread(entry.ContentId, name, world);
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
        var others = 0;
        foreach (var character in view.Characters)
        {
            if (character.IsBound || character.Stage == SharingStage.Checking)
            {
                others++;
            }
        }

        if (others == 0)
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
