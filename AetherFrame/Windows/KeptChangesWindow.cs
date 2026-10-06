using System;
using System.Numerics;
using AetherFrame.UI.Library;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// "Unsaved changes kept": the offer of an editor's unsaved changes AetherFrame kept when it last
/// unloaded, one at a time, in a small centered window like the welcome. Everything it says
/// and does is <see cref="KeptChangesOffer"/>'s. It opens once both Libraries have loaded and a
/// character is logged in; closing it without an answer keeps every change, and My Plates then
/// shows <see cref="DrawReminder"/> until they are answered.
/// </summary>
internal sealed class KeptChangesWindow : Window
{
    private const ImGuiWindowFlags OfferFlags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;

    private readonly KeptChangesOffer offer;
    private readonly AetherWindowChrome chrome = new();

    internal KeptChangesWindow(KeptChangesOffer offer)
        : base("Unsaved Changes Kept##AetherFrameKeptChanges", OfferFlags, forceMainWindow: true)
    {
        this.offer = offer;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    /// <summary>
    /// My Plates' reminder while kept changes wait for an answer: one line under its header, in the
    /// tutorial reminder's style, with Review to ask again. Draws nothing otherwise.
    /// </summary>
    internal static void DrawReminder(KeptChangesOffer offer)
    {
        if (offer.ReminderText is not { } text)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId("KeptChangesReminder");
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight() + (AetherMetrics.SpaceSm * scale);
        var width = ImGui.GetContentRegionAvail().X;
        drawList.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(AetherPalette.WarningTint), AetherMetrics.RadiusMd * scale);
        drawList.AddRectFilled(min, new Vector2(min.X + (AetherMetrics.AccentBarWidth * scale), min.Y + height), ImGui.GetColorU32(AetherPalette.Warning), AetherMetrics.RadiusMd * scale, ImDrawFlags.RoundCornersLeft);

        ImGui.SetCursorScreenPos(min + new Vector2(AetherMetrics.SpaceMd * scale, AetherMetrics.SpaceXs * scale));
        EditorWidgets.IconText(FontAwesomeIcon.UndoAlt, AetherPalette.Warning);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
        {
            ImGui.TextUnformatted(text);
        }

        ImGui.SameLine();
        if (AetherControls.PrimaryButton(KeptChangesOffer.ReviewLabel, tooltip: KeptChangesOffer.ReviewTooltip))
        {
            offer.Review();
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + height + (AetherMetrics.SpaceXs * scale)));
    }

    /// <summary>Opens when the offer asks to (a load, a login, or Review).</summary>
    public override void PreOpenCheck()
    {
        if (offer.ConsumeOpenRequest())
        {
            IsOpen = true;
            BringToFront();
        }
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + (viewport.WorkSize / 2f), ImGuiCond.Appearing, new Vector2(0.5f));
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    /// <summary>Closed (its close button, Escape, or nothing left to offer): whatever is still on offer stays kept.</summary>
    public override void OnClose() => offer.Closed();

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 420f * scale;

        if (!offer.HasCurrent)
        {
            DrawClosingMessage(width);
            return;
        }

        AetherBrand.Header(KeptChangesOffer.Title, offer.PositionText);
        ImGui.Dummy(new Vector2(width, AetherMetrics.SpaceSm * scale));

        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            if (offer.Notice is { } notice)
            {
                Line(notice, AetherPalette.Info);
                ImGui.Spacing();
            }

            Line(offer.Body, AetherPalette.TextSecondary);
            if (offer.VariantNote is { } note)
            {
                ImGui.Spacing();
                Line(note, AetherPalette.Warning);
            }

            if (offer.ConsequenceLine is { } consequence)
            {
                ImGui.Spacing();
                Line(consequence, AetherPalette.TextMuted);
            }

            if (offer.Error is { } error)
            {
                ImGui.Spacing();
                Line(error, EditorWidgets.ErrorColor);
            }
        }

        if (offer.CheckpointOptions is { } points)
        {
            ImGui.Spacing();
            DrawCheckpointChoice(points, width);
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceMd * scale));
        if (offer.Question is { } question)
        {
            DrawQuestion(question, width);
        }
        else
        {
            DrawChoices();
        }
    }

    /// <summary>
    /// The editing's recovery points, newest first: the newest is what the answer acts on unless an
    /// older one is chosen here. One window for the editing, never one per checkpoint.
    /// </summary>
    private void DrawCheckpointChoice(System.Collections.Generic.IReadOnlyList<string> points, float width)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(KeptChangesOffer.CheckpointLabel);
        EditorWidgets.Tooltip(KeptChangesOffer.CheckpointTooltip);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width - ImGui.CalcTextSize(KeptChangesOffer.CheckpointLabel).X - ImGui.GetStyle().ItemSpacing.X);
        var selected = Math.Clamp(offer.SelectedCheckpoint, 0, points.Count - 1);
        using (ImRaii.Disabled(offer.IsBusy || offer.Question is not null))
        using (var combo = ImRaii.Combo("##KeptChangesPoint", points[selected]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (ImGui.Selectable($"{points[i]}##Point{i}", i == selected))
                    {
                        offer.SelectCheckpoint(i);
                    }
                }
            }
        }
    }

    /// <summary>The choices (Recover as New Plate too beside Resume Editing; no Discard for a Plate that couldn't be opened), right-aligned.</summary>
    private void DrawChoices()
    {
        var primary = ButtonSize(offer.PrimaryLabel);
        var asNew = ButtonSize(KeptChangesOffer.RestoreAsNewLabel);
        var discard = ButtonSize(KeptChangesOffer.DiscardLabel);
        var later = ButtonSize(KeptChangesOffer.DecideLaterLabel);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var row = primary.X + spacing + later.X + (offer.OffersDiscard ? discard.X + spacing : 0f) + (offer.OffersNewPlateToo ? asNew.X + spacing : 0f);
        AetherControls.AlignRight(row);

        using (ImRaii.Disabled(offer.IsBusy))
        {
            if (AetherControls.PrimaryButton(offer.IsBusy ? "Recovering..." : offer.PrimaryLabel, primary, offer.PrimaryTooltip))
            {
                offer.Choose();
            }

            if (offer.OffersNewPlateToo)
            {
                ImGui.SameLine();
                if (AetherControls.GhostButton(KeptChangesOffer.RestoreAsNewLabel, asNew, KeptChangesOffer.RecoverAsNewSecondaryTooltip))
                {
                    offer.ChooseNewPlate();
                }
            }

            if (offer.OffersDiscard)
            {
                ImGui.SameLine();
                if (AetherControls.DangerButton(KeptChangesOffer.DiscardLabel, discard, offer.DiscardTooltipText))
                {
                    offer.Discard();
                }
            }

            ImGui.SameLine();
            if (AetherControls.GhostButton(KeptChangesOffer.DecideLaterLabel, later, KeptChangesOffer.DecideLaterTooltip))
            {
                offer.DecideLater();
            }
        }
    }

    /// <summary>
    /// The open Plate's own unsaved changes stand in the way: Save, Discard or Cancel, as My Plates
    /// asks. This window isn't modal, so the offer drops the question once that Plate isn't open with
    /// those changes any more, and its buttons never act on another (see <see cref="KeptChangesOffer.Advance"/>).
    /// </summary>
    private void DrawQuestion(string question, float width)
    {
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            Line(question, AetherPalette.TextPrimary);
        }

        ImGui.Spacing();
        var size = ButtonSize("Saving...");
        AetherControls.AlignRight((size.X * 3f) + (ImGui.GetStyle().ItemSpacing.X * 2f));
        using (ImRaii.Disabled(!offer.CanAnswerQuestion))
        {
            if (AetherControls.PrimaryButton(offer.IsSavingForQuestion ? "Saving..." : "Save", size))
            {
                offer.AnswerSave();
            }

            ImGui.SameLine();
            if (AetherControls.DangerButton("Discard##Question", size))
            {
                offer.AnswerDiscard();
            }
        }

        // Cancel stays usable while the question's Save is written: the save finishes, and nothing is restored.
        ImGui.SameLine();
        if (AetherControls.GhostButton("Cancel", size))
        {
            offer.AnswerCancel();
        }
    }

    /// <summary>Nothing left on offer: a last message to read, if there is one, then the window closes.</summary>
    private void DrawClosingMessage(float width)
    {
        var message = offer.Error ?? offer.Notice;
        if (message is null)
        {
            IsOpen = false;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        AetherBrand.Header(KeptChangesOffer.Title);
        ImGui.Dummy(new Vector2(width, AetherMetrics.SpaceSm * scale));
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            Line(message, offer.Error is null ? AetherPalette.TextSecondary : EditorWidgets.ErrorColor);
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceMd * scale));
        var size = ButtonSize("OK");
        AetherControls.AlignRight(size.X);
        if (AetherControls.PrimaryButton("OK", size))
        {
            offer.DismissMessages();
            IsOpen = false;
        }
    }

    /// <summary>A dialog button: 120 px at the interface's scale, wider only for a label that needs it.</summary>
    private static Vector2 ButtonSize(string label)
    {
        var minimum = AetherMetrics.DialogButtonWidth * ImGuiHelpers.GlobalScale;
        var needed = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);
        return new Vector2(Math.Max(minimum, needed), 0f);
    }

    /// <summary>Text that may hold a Plate's name: never formatted, wrapped at the pushed position.</summary>
    private static void Line(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted(text);
        }
    }
}
