using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// Templates in My Plates: Use Template as My Plates does it, and the quieter Manage Templates
/// mode the Create Plate chooser opens into (the Templates card grid and its action bar: View /
/// Use Template / Rename / Duplicate / Delete). The chooser itself, the primary Template entry point
/// (Start with a built-in, or from My Templates), and its Rename and Delete Template prompts belong
/// to the shared <see cref="PlateMenu"/> (<see cref="TemplateChooser"/>), which the editors' New
/// Plate... draws too (interface task 2); Save as Template is a Plate menu action. Templates is
/// deliberately not a permanent top-level tab beside My Plates: Manage Templates is entered only
/// from the chooser and returns to My Plates explicitly. Follows the exact visual and interaction
/// patterns <c>PlateLibraryWindow.cs</c>/<c>.Actions.cs</c> already established for My Plates: the
/// same card grid math, the same async-operation and popup-deferral plumbing, the same
/// disabled-with-tooltip convention for actions a built-in Template can't do.
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    /// <summary>Not a permanent tab — see the class doc. My Plates is always the entry point;
    /// Templates is a mode entered from the Create Plate chooser's Manage Templates action.</summary>
    private enum LibraryView
    {
        MyPlates,
        Templates,
    }

    private static readonly Vector4 BuiltInBadgeColor = new(0.55f, 0.62f, 0.95f, 1f);
    private static readonly Vector4 UserTemplateBadgeColor = new(0.55f, 0.85f, 0.65f, 1f);

    private readonly Dictionary<Guid, string> templateCardIds = new();

    // A built-in Template's document is generated on every request; its background never changes,
    // so the fallback thumbnail reads it once per Template instead of once per frame.
    private readonly Dictionary<Guid, ProfileBackground?> builtInBackgrounds = new();

    private LibraryView activeView = LibraryView.MyPlates;
    private Guid? selectedTemplateId;
    private string templateSearchText = string.Empty;

    // The editors' chooser asked for Manage Templates: OnOpen opens the window on it, once.
    private bool openOnManageTemplates;

    /// <summary>Whether the Create Plate chooser was on screen this frame or the one before (the tutorial waits on it).</summary>
    internal bool TemplateChooserShowing => plateMenu.Chooser.Showing;

    /// <summary>Whether Manage Templates is the view showing (the tutorial reads it, never sets it).</summary>
    internal bool TemplatesViewShowing => activeView == LibraryView.Templates;

    /// <summary>
    /// The editors' Create Plate chooser's Manage Templates...: this window, open on Manage
    /// Templates and in front, as the chooser's link opens it here.
    /// </summary>
    internal void ShowManageTemplates()
    {
        activeView = LibraryView.Templates;
        selectedTemplateId = null;
        openOnManageTemplates = true;
        IsOpen = true;
        BringToFront();
    }

    /// <summary>
    /// The shared Create Plate chooser, as My Plates uses it: Use Template makes the Plate, then
    /// opens it (<see cref="UseTemplate"/>); Manage Templates... switches this window to its
    /// Templates view; and Manage Templates' selection follows a row menu's Duplicate and Delete.
    /// </summary>
    private void AttachTemplateChooser()
    {
        var chooser = plateMenu.Chooser;
        chooser.Use = UseTemplate;
        chooser.ManageTemplates = () =>
        {
            activeView = LibraryView.Templates;
            selectedTemplateId = null;
        };
        chooser.TemplateDuplicated = templateId => selectedTemplateId = templateId;
        chooser.TemplateDeleted = templateId =>
        {
            if (selectedTemplateId == templateId)
            {
                selectedTemplateId = null;
            }
        };
    }

    // ---------------------------------------------------------------- Manage Templates (a mode, not a tab)

    private void DrawTemplatesView()
    {
        var allTemplates = templates.GetOrderedTemplates();
        if (selectedTemplateId is { } selected && allTemplates.All(t => t.TemplateId != selected))
        {
            selectedTemplateId = null;
        }

        DrawTemplateHeader(allTemplates.Count);
        if (templates.LoadFailed)
        {
            ImGui.TextColored(EditorWidgets.WarningColor, TemplateChooser.UserTemplatesUnavailableText);
        }

        ImGui.Separator();

        var shown = string.IsNullOrWhiteSpace(templateSearchText) ? allTemplates : templates.Search(templateSearchText);
        var footerHeight = (ImGui.GetFrameHeightWithSpacing() * 2f) + ImGui.GetStyle().ItemSpacing.Y + EditorWidgets.Scaled(4f);
        using (var grid = ImRaii.Child("##TemplateGrid", new Vector2(-1, -footerHeight), false))
        {
            if (grid.Success)
            {
                DrawTemplateGrid(shown, allTemplates.Count);
            }
        }

        ImGui.Separator();
        DrawTemplateActionBar();
    }

    private void DrawTemplateHeader(int templateCount)
    {
        if (ImGui.ArrowButton("##BackToMyPlates", ImGuiDir.Left))
        {
            activeView = LibraryView.MyPlates;
            selectedTemplateId = null;
        }

        EditorWidgets.Tooltip("Back to My Plates");

        ImGui.SameLine();
        ImGui.TextUnformatted("Manage Templates");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(EditorWidgets.Scaled(220f));
        ImGui.InputTextWithHint("##TemplateSearch", "Search Templates...", ref templateSearchText, 64);

        var countText = templateCount == 1 ? "1 Template" : $"{templateCount} Templates";
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(countText).X));
        ImGui.TextDisabled(countText);
    }

    private void DrawTemplateGrid(IReadOnlyList<TemplateSummary> shown, int totalCount)
    {
        if (totalCount == 0)
        {
            ImGui.TextDisabled("No Templates yet.");
            return;
        }

        if (shown.Count == 0)
        {
            ImGui.TextDisabled("No Templates match your search.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((available + spacing.X) / (CardWidth + spacing.X)));

        for (var i = 0; i < shown.Count; i++)
        {
            if (i % columns != 0)
            {
                ImGui.SameLine();
            }

            DrawTemplateCard(shown[i]);
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            selectedTemplateId = null;
        }
    }

    private void DrawTemplateCard(TemplateSummary template)
    {
        if (!templateCardIds.TryGetValue(template.TemplateId, out var id))
        {
            id = "##TemplateCard" + template.TemplateId.ToString("N");
            templateCardIds[template.TemplateId] = id;
        }

        var thumbnailSize = new Vector2(CardWidth - (CardPadding * 2f), (CardWidth - (CardPadding * 2f)) / ThumbnailAspect);
        var cardSize = new Vector2(CardWidth, thumbnailSize.Y + (CardPadding * 3f) + ImGui.GetTextLineHeight());

        ImGui.InvisibleButton(id, cardSize);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var isSelected = selectedTemplateId == template.TemplateId;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            selectedTemplateId = template.TemplateId;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && template.IsReady)
        {
            UseTemplate(template.TemplateId);
        }

        if (hovered && CardTooltip(template.Problem, template.HasUnsupportedElements) is { } tooltip)
        {
            ImGui.SetTooltip(tooltip);
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? CardHoverColor : CardColor), 6f);
        if (isSelected)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(EditorWidgets.AccentColor), 6f, ImDrawFlags.None, 2f);
        }

        var thumbnailMin = min + new Vector2(CardPadding);
        var thumbnailMax = thumbnailMin + thumbnailSize;
        DrawTemplateThumbnail(drawList, template, thumbnailMin, thumbnailMax);
        DrawTemplateKindBadge(drawList, template, thumbnailMin, thumbnailMax);

        if (template.HasUnsupportedElements)
        {
            DrawCompatibilityMarker(drawList, thumbnailMin, thumbnailMax);
        }

        var textPos = new Vector2(thumbnailMin.X, thumbnailMax.Y + CardPadding);
        drawList.PushClipRect(textPos, new Vector2(thumbnailMax.X, max.Y), true);
        var nameColor = template.IsReady ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(EditorWidgets.DimTextColor);
        drawList.AddText(textPos, nameColor, template.DisplayName);
        drawList.PopClipRect();
    }

    /// <summary>The thumbnail when one is Ready and loads; otherwise a fallback from the Template's
    /// own background. Hits the exact same (currently non-functional) thumbnail pipeline as Plates
    /// — a separate cache directory only, never a second mechanism.</summary>
    private void DrawTemplateThumbnail(ImDrawListPtr drawList, TemplateSummary template, Vector2 min, Vector2 max)
    {
        if (template.IsReady)
        {
            var versionKey = PlateThumbnailService.VersionKeyFor(0, template.ModifiedUtc);
            var thumbnail = templateThumbnails.Get(template.TemplateId, versionKey, () => templates.GetSavedDocument(template.TemplateId));

            if (templateThumbnailTextures.GetWrapOrNull(template.TemplateId, thumbnail) is { } wrap)
            {
                drawList.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFFu);
                return;
            }
        }

        DrawTemplateFallbackThumbnail(drawList, template, min, max);
    }

    private void DrawTemplateFallbackThumbnail(ImDrawListPtr drawList, TemplateSummary template, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);

        var background = template.IsReady ? SavedBackgroundOf(template) : null;
        var icon = FontAwesomeIcon.Copy;

        if (!template.IsReady)
        {
            icon = FontAwesomeIcon.ExclamationTriangle;
        }
        else if (background is { } style)
        {
            var opacity = Math.Clamp(style.Opacity, 0f, 1f);
            var primary = style.PrimaryColor with { W = style.PrimaryColor.W * opacity };
            var secondary = style.SecondaryColor with { W = style.SecondaryColor.W * opacity };

            switch (style.Mode)
            {
                case ProfileBackgroundMode.SolidColor:
                case ProfileBackgroundMode.TexturedFill:
                    drawList.AddRectFilled(min, max, ImGui.GetColorU32(primary), 4f);
                    break;

                case ProfileBackgroundMode.LinearGradient:
                    var mixed = Vector4.Lerp(primary, secondary, 0.5f);
                    drawList.AddRectFilledMultiColor(min, max, ImGui.GetColorU32(primary), ImGui.GetColorU32(mixed), ImGui.GetColorU32(secondary), ImGui.GetColorU32(mixed));
                    break;

                case ProfileBackgroundMode.Image:
                    icon = FontAwesomeIcon.Image;
                    break;
            }
        }

        var iconText = EditorWidgets.GetIconString(icon);
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText((min + max - iconSize) / 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)), iconText);
        }
    }

    /// <summary>The Template's saved background, read only: a user Template's is the saved
    /// document's own; a built-in one's is kept from the first request.</summary>
    private ProfileBackground? SavedBackgroundOf(TemplateSummary template)
    {
        if (!template.IsBuiltIn)
        {
            return templates.GetSavedDocument(template.TemplateId)?.Background;
        }

        if (!builtInBackgrounds.TryGetValue(template.TemplateId, out var background))
        {
            background = templates.GetSavedDocument(template.TemplateId)?.Background;
            builtInBackgrounds[template.TemplateId] = background;
        }

        return background;
    }

    private static void DrawTemplateKindBadge(ImDrawListPtr drawList, TemplateSummary template, Vector2 thumbnailMin, Vector2 thumbnailMax)
    {
        var label = template.IsBuiltIn ? "Built In" : "My Template";
        var color = template.IsBuiltIn ? BuiltInBadgeColor : UserTemplateBadgeColor;
        var textSize = ImGui.CalcTextSize(label);
        var padding = EditorWidgets.Scaled(new Vector2(6f, 2f));
        var inset = EditorWidgets.Scaled(4f);
        var badgeMax = new Vector2(thumbnailMax.X - inset, thumbnailMin.Y + inset + textSize.Y + (padding.Y * 2f));
        var badgeMin = new Vector2(badgeMax.X - textSize.X - (padding.X * 2f), thumbnailMin.Y + inset);

        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(color), 4f);
        drawList.AddText(badgeMin + padding, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.07f, 1f)), label);
    }

    private void DrawTemplateActionBar()
    {
        var selected = selectedTemplateId is { } id ? templates.FindTemplate(id) : null;
        var ready = selected is { IsReady: true };

        using (ImRaii.Disabled(!ready || selected is { SupportsPreview: false }))
        {
            if (ImGui.Button("View"))
            {
                var document = templates.GetSavedDocument(selected!.TemplateId, new PlateStarterContent(characterIdentity.CurrentInfo));
                if (document is not null)
                {
                    showDocumentInViewer(document);
                }
            }
        }

        EditorWidgets.Tooltip(selected is { SupportsPreview: false }
            ? "This Template has nothing to show."
            : "Show this Template in the Plate Viewer. Nothing about it changes.");

        ImGui.SameLine();
        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.Button("Use Template"))
            {
                UseTemplate(selected!.TemplateId);
            }

            EditorWidgets.Tooltip("Create a new, independent Plate from this Template.");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(selected is null || selected.IsBuiltIn || IsBusy))
        {
            // The same prompts and actions as the chooser's row menus: the shared PlateMenu's.
            if (ImGui.Button("Rename"))
            {
                plateMenu.Chooser.RequestRename(selected!.TemplateId, selected.DisplayName);
            }

            ImGui.SameLine();
            if (ImGui.Button("Duplicate"))
            {
                plateMenu.Actions.DuplicateTemplate(selected!.TemplateId, newId => selectedTemplateId = newId);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                plateMenu.Chooser.RequestDelete(selected!.TemplateId);
            }
        }

        if (selected is { IsBuiltIn: true })
        {
            EditorWidgets.Tooltip("Built-in Templates can't be renamed, duplicated, or deleted.");
        }

        // Second row: what's happening, or what went wrong.
        if (IsBusy)
        {
            ImGui.TextDisabled("Working...");
        }
        else if (runner.Error is { } error)
        {
            ImGui.TextColored(EditorWidgets.ErrorColor, error);
        }
        else if (runner.Status is { } status)
        {
            ImGui.TextColored(EditorWidgets.SuccessColor with { W = 0.85f }, status);
        }
        else if (selected is { } template)
        {
            ImGui.TextDisabled(template.IsReady
                ? (template.IsBuiltIn ? "Ships with AetherFrame." : $"Saved {template.ModifiedUtc.ToLocalTime():g}")
                : template.Problem ?? "This Template can't be opened.");
        }
        else
        {
            ImGui.TextDisabled("Select a Template.");
        }
    }

    /// <summary>
    /// Use Template, as My Plates does it: makes the new Plate first (see
    /// <see cref="PlateActions.UseTemplate"/>), selects its card, then opens it in Basic or Advanced
    /// depending on whether its content actually has Basic structure (see
    /// <see cref="EditorSurfaceChooser"/>, the same rule as Edit), never a fixed choice per Template
    /// kind, so an arbitrary user Template opens sensibly. Opening it asks about the open Plate's
    /// unsaved changes, after the Plate is made; interface task 10 moves the question before it, as
    /// the editors' New Plate already asks (<see cref="PlateSwitcher"/>).
    /// </summary>
    private void UseTemplate(Guid templateId)
    {
        var starter = new PlateStarterContent(characterIdentity.CurrentInfo);
        plateMenu.Actions.UseTemplate(templateId, characterIdentity.CurrentCharacter, starter, result =>
        {
            activeView = LibraryView.MyPlates;
            selectedPlateId = result.PlateId;
            searchText = string.Empty;
            var basic = EditorSurfaceChooser.ForDocument(library.GetSavedDocument(result.PlateId)) == EditorSurfaceKind.Basic;
            RequestOpen(result.PlateId, basic);
        });
    }
}
