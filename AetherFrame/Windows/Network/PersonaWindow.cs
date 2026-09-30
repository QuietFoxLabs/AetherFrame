using System;
using System.Collections.Generic;
using System.Numerics;
using AetherFrame.Personas;
using AetherFrame.Services.Network.Personas;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The persona window (NETWORK2's N2-5c; docs/networking/DecisionRegister.md, D3, D9a, K2, K4 and
/// L12): the player's personas and the one in use; making, naming and switching them; K4's
/// acknowledgement with K2's disclosure; and the key files no persona uses. It reads the session's
/// view each frame, which never waits, and hands every change to the session, which runs it off
/// the framework thread; it never holds the manager. A label is the player's own text, so it is
/// only ever drawn unformatted, never inside an ImGui label, a tooltip or a format string.
/// Compiled only in the networking preview flavour.
/// </summary>
internal sealed class PersonaWindow : Window
{
    private const int NameBufferBytes = 256;

    private readonly PersonaSession session;
    private readonly string keysDirectory;
    private readonly string registryPath;
    private readonly string applicationData;
    private readonly string userProfile;
    private readonly AetherWindowChrome chrome = new();
    private readonly Dictionary<PersonaSlotId, PersonaKeyCheck> checks = new();
    private readonly Dictionary<PersonaSlotId, string> restoreNames = new();
    private string newName = "";
    private PersonaSlotId renaming;
    private string renameText = "";
    private PersonaSlotId acknowledging;
    private bool revealAcknowledgement;
    private bool understood;
    private (PersonaAction Action, PersonaSlotId Slot, string? Label)? pending;
    private PersonaOperationOutcome? handled;
    private PersonaAudit? seenAudit;
    private string? message;
    private bool messageIsProblem;

    internal PersonaWindow(PersonaSession session, string keysDirectory, string registryPath)
        : base("Personas##AetherFramePersonas", ImGuiWindowFlags.NoCollapse)
    {
        this.session = session;
        this.keysDirectory = keysDirectory;
        this.registryPath = registryPath;
        applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460f, 320f),
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
        var view = session.View;

        // A check describes the key files as one audit found them: a newer audit, after a create,
        // a restore, a check, a refresh or a retry, may hold different files under the same names,
        // so what was checked is checked again rather than trusted (L12). A check's own audit
        // arrives with its outcome, which is taken below, so its result survives this.
        if (!ReferenceEquals(view.Audit, seenAudit))
        {
            checks.Clear();
            seenAudit = view.Audit;
        }

        TakeOutcome(view);
        Wrapped(PersonaWindowModel.Intro, AetherPalette.TextMuted);
        AetherControls.Divider();
        switch (view.State)
        {
            case PersonaSessionState.Starting:
                AetherControls.StatusLine(AetherTone.Info, PersonaWindowModel.Starting);
                break;
            case PersonaSessionState.Unavailable:
                DrawUnavailable(view);
                break;
            case PersonaSessionState.Ready:
                DrawReady(view);
                break;
        }
    }

    /// <summary>Draws text wrapped to the window, unformatted: safe for a label, whatever it holds.</summary>
    private static void Wrapped(string text, Vector4? color = null)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Takes the outcome of the operation this window started, once, and turns it into what the window shows.</summary>
    private void TakeOutcome(PersonaSessionView view)
    {
        if (view.Busy || pending is not { } asked || view.LastOutcome is not { } outcome || ReferenceEquals(outcome, handled))
        {
            return;
        }

        handled = outcome;
        pending = null;
        if (outcome.Succeeded)
        {
            switch (asked.Action)
            {
                case PersonaAction.CheckKey:
                    checks[asked.Slot] = new PersonaKeyCheck(outcome.Opened);
                    break;
                case PersonaAction.Create when outcome.Persona is { } created:
                    // K4 asks for the acknowledgement whenever a persona is made.
                    Acknowledge(created.Slot);
                    newName = "";
                    break;
                case PersonaAction.Acknowledge:
                    acknowledging = default;
                    break;
                case PersonaAction.Rename:
                    renaming = default;
                    break;
                case PersonaAction.RestoreKey:
                    restoreNames.Remove(asked.Slot);
                    checks.Remove(asked.Slot);

                    // A restored persona starts without K4's acknowledgement, as a new one does.
                    if (outcome.Persona is { } restored)
                    {
                        Acknowledge(restored.Slot);
                    }

                    break;
            }
        }

        message = PersonaWindowModel.Message(asked.Action, outcome, session.Active, asked.Label);
        messageIsProblem = !outcome.Succeeded;
    }

    /// <summary>Opens K4's step for a persona, scrolled into view on the next frame.</summary>
    private void Acknowledge(PersonaSlotId slot)
    {
        acknowledging = slot;
        understood = false;
        revealAcknowledgement = true;
    }

    private void Start(PersonaAction action, PersonaSlotId slot = default, string? label = null)
    {
        if (session.TryStart(PersonaWindowModel.LogName(action), PersonaWindowModel.Work(action, slot, label), PersonaWindowModel.AuditsAfterFailure(action)))
        {
            pending = (action, slot, label);
            message = null;
        }
    }

    private void DrawUnavailable(PersonaSessionView view)
    {
        AetherControls.Callout(AetherTone.Warning, view.Message ?? PersonaWindowModel.Starting);
        if (view.Reason == PersonaUnavailableReason.RegistryUnreadable)
        {
            DrawPath("Your persona list", "list", registryPath);
            Wrapped(PersonaWindowModel.RegistryAside, AetherPalette.TextMuted);
        }

        if (view.CanRetry)
        {
            using (ImRaii.Disabled(view.Busy))
            {
                if (AetherControls.PrimaryButton("Try again"))
                {
                    session.Retry();
                }
            }
        }
    }

    private void DrawReady(PersonaSessionView view)
    {
        var personas = session.Personas;
        var active = session.Active;
        Wrapped(PersonaWindowModel.InUse(active));
        if (active is not null)
        {
            ImGui.SameLine();
            AetherControls.MutedInline(PersonaWindowModel.ShortIdentity(active.Id));
            AetherControls.Tooltip(active.Id.ToString());
        }

        DrawMessage(view);
        using (ImRaii.Disabled(view.Busy))
        {
            AetherControls.SectionHeader("Your personas");
            if (personas.Count == 0)
            {
                Wrapped("You have no personas yet. Create one below; it isn't used until you choose Use.", AetherPalette.TextMuted);
            }

            for (var index = 0; index < personas.Count; index++)
            {
                DrawPersona(personas[index], active);
            }

            DrawAcknowledgement(personas);

            AetherControls.SectionHeader("Create a persona");
            DrawCreate();

            if (view.Audit is { } audit)
            {
                DrawOrphans(audit, personas);
                DrawUnusable(audit, personas);
            }

            AetherControls.SectionHeader("Where your keys are");
            DrawPath("Key folder", "keys", keysDirectory);
            Wrapped(PersonaWindowModel.KeysWarning, AetherPalette.TextMuted);
            if (AetherControls.GhostButton("Look at the key files again"))
            {
                Start(PersonaAction.Refresh);
            }
        }
    }

    private void DrawMessage(PersonaSessionView view)
    {
        if (view.Busy)
        {
            AetherControls.StatusLine(AetherTone.Info, "Working...");
            return;
        }

        if (message is null)
        {
            return;
        }

        if (messageIsProblem)
        {
            AetherControls.Callout(AetherTone.Warning, message);
        }
        else
        {
            AetherControls.StatusLine(AetherTone.Success, message);
        }
    }

    private void DrawPersona(PersonaRecord persona, PersonaRecord? active)
    {
        using var id = ImRaii.PushId(persona.Slot.ToString());
        var inUse = active is not null && active.Slot == persona.Slot;
        if (renaming == persona.Slot)
        {
            ImGui.SetNextItemWidth(EditorWidgets.Scaled(240f));
            ImGui.InputText("##rename", ref renameText, NameBufferBytes);
            ImGui.SameLine();
            if (AetherControls.SecondaryButton("Save"))
            {
                Start(PersonaAction.Rename, persona.Slot, renameText);
            }

            ImGui.SameLine();
            if (AetherControls.GhostButton("Cancel"))
            {
                renaming = default;
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(persona.Label);
        }

        ImGui.SameLine();
        AetherControls.MutedInline(PersonaWindowModel.ShortIdentity(persona.Id));
        AetherControls.Tooltip(persona.Id.ToString());
        if (inUse)
        {
            ImGui.SameLine();
            AetherControls.Pill("In use", AetherPalette.Aether, AetherPalette.TextOnAccent);
        }

        if (inUse)
        {
            if (AetherControls.SecondaryButton("Stop using"))
            {
                Start(PersonaAction.StopUsing);
            }
        }
        else if (AetherControls.SecondaryButton("Use"))
        {
            Start(PersonaAction.Use, persona.Slot, persona.Label);
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Rename"))
        {
            renaming = persona.Slot;
            renameText = persona.Label;
        }

        if (!persona.Acknowledged)
        {
            ImGui.SameLine();
            if (AetherControls.GhostButton("Before first share..."))
            {
                Acknowledge(persona.Slot);
            }
        }

        ImGui.SameLine();
        AetherControls.MutedInline(PersonaWindowModel.KeyFileName(persona.Slot));
        AetherControls.Tooltip("The file this persona's key is kept in. Removing it makes the persona unusable for good.");
        AetherControls.Divider();
    }

    private void DrawAcknowledgement(IReadOnlyList<PersonaRecord> personas)
    {
        if (acknowledging.IsEmpty)
        {
            return;
        }

        PersonaRecord? persona = null;
        for (var index = 0; index < personas.Count; index++)
        {
            if (personas[index].Slot == acknowledging)
            {
                persona = personas[index];
            }
        }

        if (persona is null || persona.Acknowledged)
        {
            acknowledging = default;
            revealAcknowledgement = false;
            return;
        }

        // The step sits below the list, which can be long: when it opens, scroll it to the top of
        // the window rather than leave it out of sight.
        if (revealAcknowledgement)
        {
            ImGui.SetScrollHereY(0f);
            revealAcknowledgement = false;
        }

        using var id = ImRaii.PushId("acknowledge");
        AetherControls.Heading(PersonaWindowModel.K4Title(persona));
        Wrapped(PersonaWindowModel.K4Text);
        ImGui.Spacing();
        Wrapped(PersonaWindowModel.K2Disclosure, AetherPalette.TextSecondary);
        ImGui.Spacing();
        ImGui.Checkbox("I understand", ref understood);
        using (ImRaii.Disabled(!understood))
        {
            if (AetherControls.PrimaryButton("Acknowledge"))
            {
                Start(PersonaAction.Acknowledge, persona.Slot, persona.Label);
            }
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Not now"))
        {
            acknowledging = default;
        }

        AetherControls.Divider();
    }

    private void DrawCreate()
    {
        using var id = ImRaii.PushId("create");
        ImGui.SetNextItemWidth(EditorWidgets.Scaled(260f));
        ImGui.InputTextWithHint("##name", "A name only you see, like Main or RP alt", ref newName, NameBufferBytes);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newName)))
        {
            if (AetherControls.PrimaryButton("Create persona"))
            {
                Start(PersonaAction.Create, default, newName);
            }
        }
    }

    private void DrawOrphans(PersonaAudit audit, IReadOnlyList<PersonaRecord> personas)
    {
        if (audit.Orphans.Count == 0 && !audit.ListingFailed)
        {
            return;
        }

        AetherControls.SectionHeader("Keys without a persona");
        if (audit.ListingFailed)
        {
            AetherControls.Callout(AetherTone.Warning, "AetherFrame couldn't list the key folder, so a key without a persona may not show here.");
        }

        for (var index = 0; index < audit.Orphans.Count; index++)
        {
            var orphan = audit.Orphans[index];
            var row = PersonaWindowModel.Describe(orphan, personas, checks.GetValueOrDefault(orphan.Slot));
            using var id = ImRaii.PushId("orphan" + row.Slot);
            ImGui.TextUnformatted(row.KeyFile);
            Wrapped(row.Description, AetherPalette.TextSecondary);
            if (row.CanCheck)
            {
                if (AetherControls.SecondaryButton("Check"))
                {
                    Start(PersonaAction.CheckKey, row.Slot);
                }

                if (row.CanRestore)
                {
                    ImGui.SameLine();
                }
            }

            if (row.CanRestore)
            {
                var name = restoreNames.GetValueOrDefault(row.Slot, "");
                ImGui.SetNextItemWidth(EditorWidgets.Scaled(200f));
                if (ImGui.InputTextWithHint("##restoreName", "A name for it", ref name, NameBufferBytes))
                {
                    restoreNames[row.Slot] = name;
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(name)))
                {
                    if (AetherControls.SecondaryButton("Restore as persona"))
                    {
                        Start(PersonaAction.RestoreKey, row.Slot, name);
                    }
                }
            }

            AetherControls.Divider();
        }
    }

    private static void DrawUnusable(PersonaAudit audit, IReadOnlyList<PersonaRecord> personas)
    {
        if (audit.Unusable.Count == 0)
        {
            return;
        }

        AetherControls.SectionHeader("Personas whose key can't be used");
        for (var index = 0; index < audit.Unusable.Count; index++)
        {
            var unusable = audit.Unusable[index];

            // The audit keeps the record as it was then; a rename since shows under its new name.
            var label = unusable.Record.Label;
            for (var current = 0; current < personas.Count; current++)
            {
                if (personas[current].Slot == unusable.Record.Slot)
                {
                    label = personas[current].Label;
                }
            }

            ImGui.TextUnformatted(label);
            Wrapped(PersonaWindowModel.Describe(unusable.Reason), AetherPalette.TextSecondary);
        }
    }

    /// <summary>A path as the window shows it, from <c>%APPDATA%</c> on when it lies there, with a button that copies that same form.</summary>
    private void DrawPath(string caption, string id, string path)
    {
        var shown = PersonaWindowModel.DisplayPath(path, applicationData, userProfile);
        using var pushed = ImRaii.PushId(id);
        ImGui.TextUnformatted(caption);
        Wrapped(shown, AetherPalette.TextSecondary);
        if (AetherControls.GhostButton("Copy path"))
        {
            ImGui.SetClipboardText(shown);
        }
    }
}
