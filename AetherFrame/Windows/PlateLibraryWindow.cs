using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using AetherFrame.Services.Thumbnails;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates: the visual collection of every saved Plate, and the way into the editors and the
/// Plate Viewer. Cards show a thumbnail (or a fallback built from the Plate's own background),
/// the name, and whether it's the current character's Active Plate; a card's right-click menu holds
/// its actions (the shared <see cref="PlateMenu"/>, which the editors' Plate menu uses too). Split
/// across partial files: this one (lifecycle, header, card grid), <c>.Actions.cs</c> (the footer and
/// opening Plates), <c>.Templates.cs</c> (the Create Plate chooser and Manage Templates), and
/// <c>.Packages.cs</c> (Import of .aetherframe files; Export is the Plate menu's).
///
/// Never shows technical identifiers (ids, versions, file names, revisions). Works with no
/// character logged in — only Set Active needs one.
/// </summary>
internal sealed partial class PlateLibraryWindow : Window, IDisposable
{
    // Card sizes in unscaled pixels, at Dalamud's global UI scale when drawn, so a card keeps room
    // for its (scaled) name on a high-DPI screen.
    private static float CardWidth => EditorWidgets.Scaled(196f);
    private static float CardPadding => EditorWidgets.Scaled(8f);
    private const float ThumbnailAspect = 16f / 9f;
    private const string CardDragPayloadType = "AF_PLATE";

    // The window's minimum size, and its size the first time it opens: room for four cards across.
    private static readonly Vector2 MinimumWindowSize = new(560f, 440f);
    private static readonly Vector2 FirstUseSize = new(880f, 620f);

    private static readonly byte[] CardDragPayload = [1];
    private static readonly Vector4 CardColor = new(1f, 1f, 1f, 0.04f);
    private static readonly Vector4 CardHoverColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 FallbackBackdropColor = new(0.16f, 0.17f, 0.21f, 1f);

    private readonly PlateLibraryService library;
    private readonly TemplateLibraryService templates;
    private readonly ProfileService profileService;
    private readonly EditorSession editorSession;
    private readonly CharacterIdentityService characterIdentity;
    private readonly PlateThumbnailService thumbnails;
    private readonly PlateThumbnailTextures thumbnailTextures;
    private readonly PlateThumbnailService templateThumbnails;
    private readonly PlateThumbnailTextures templateThumbnailTextures;
    private readonly ProfileRenderResources renderResources;

    // My Plates card previews: the saved documents they draw, by Plate id and version (see
    // PlateCardPreviewCache), and the Plates the grid listed this frame (to drop the rest).
    private readonly PlateCardPreviewCache cardPreviews = new();
    private readonly HashSet<Guid> listedPlateIds = new();
    private readonly Action openBasicEditor;
    private readonly Action openAdvancedEditor;
    private readonly Func<EditorSurfaceKind?> activeEditor;
    private readonly AdvancedEntryGate advancedEntry;
    private readonly Action<Guid> showInViewer;
    private readonly Action<ProfileDocument> showDocumentInViewer;
    private readonly PlatePackageService packages;
    private readonly FileDialogManager fileDialogManager;
    private readonly Action<string> beginImport;

    private readonly Dictionary<Guid, string> cardIds = new();

    // AetherFrame's style around this window's frame, and the tutorial's window policy.
    private readonly AetherWindowChrome chrome = new();

    // This window's Library operations, a card's menu with its prompts, and the unsaved-changes
    // question before another Plate opens.
    private readonly PlateOperationRunner runner;
    private readonly PlateMenu plateMenu;
    private readonly PlateOpenGuard openGuard;

    private Guid? selectedPlateId;
    private string searchText = string.Empty;
    private Guid? dragSourcePlateId;

    internal PlateLibraryWindow(
        PlateLibraryService library,
        TemplateLibraryService templates,
        ProfileService profileService,
        EditorSession editorSession,
        CharacterIdentityService characterIdentity,
        PlateThumbnailService thumbnails,
        PlateThumbnailTextures thumbnailTextures,
        PlateThumbnailService templateThumbnails,
        PlateThumbnailTextures templateThumbnailTextures,
        ProfileRenderResources renderResources,
        Action openBasicEditor,
        Action openAdvancedEditor,
        Func<EditorSurfaceKind?> activeEditor,
        BasicGuidance basicGuidance,
        Action<Guid> showInViewer,
        Action<ProfileDocument> showDocumentInViewer,
        PlatePackageService packages,
        FileDialogManager fileDialogManager,
        Action<string> beginImport,
        IAetherFrameLog log)
        : base("My Plates##AetherFramePlateLibrary")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.library = library;
        this.templates = templates;
        this.profileService = profileService;
        this.editorSession = editorSession;
        this.characterIdentity = characterIdentity;
        this.thumbnails = thumbnails;
        this.thumbnailTextures = thumbnailTextures;
        this.templateThumbnails = templateThumbnails;
        this.templateThumbnailTextures = templateThumbnailTextures;
        this.renderResources = renderResources;
        this.openBasicEditor = openBasicEditor;
        this.openAdvancedEditor = openAdvancedEditor;
        this.activeEditor = activeEditor;
        advancedEntry = new AdvancedEntryGate(basicGuidance);
        this.showInViewer = showInViewer;
        this.showDocumentInViewer = showDocumentInViewer;
        this.packages = packages;
        this.fileDialogManager = fileDialogManager;
        this.beginImport = beginImport;

        runner = new PlateOperationRunner(log);
        plateMenu = new PlateMenu(
            new PlateActions(library, templates, packages, profileService, editorSession, runner, log),
            library, profileService, editorSession, thumbnails, fileDialogManager)
        {
            Duplicated = copyId => selectedPlateId = copyId,
            Deleted = plateId =>
            {
                if (selectedPlateId == plateId)
                {
                    selectedPlateId = null;
                }
            },
        };
        openGuard = new PlateOpenGuard(profileService, editorSession);
        plateMenu.AttachOpenGuard(openGuard, open => OpenNow(open.PlateId, open.Basic));
    }

    /// <summary>The Help menu (tutorial, shortcuts, commands), set by the plugin once the tutorial exists.</summary>
    internal HelpMenu? Help { get; set; }

    /// <summary>Unsaved changes AetherFrame kept when it last unloaded: a reminder under the header while they wait for an answer.</summary>
    internal KeptChangesOffer? KeptChanges { get; set; }

    /// <summary>Opens the sharing window, when this build has one; the header shows a Sharing button only then.</summary>
    internal Action? OpenSharing { get; set; }

    /// <summary>Whether a Plate is the one the server shows for the logged-in character (C3), when this build shares; its card is marked Shared.</summary>
    internal Func<Guid, bool>? IsShared { get; set; }

    /// <summary>Whether a Plate is the sharing character's Active Plate but not the one the server shows yet; its card is marked Not shared yet.</summary>
    internal Func<Guid, bool>? IsNotSharedYet { get; set; }

    /// <summary>Checks what sharing a Plate would send, when this build can; a Plate's menu shows the item only then.</summary>
    internal Action<Guid>? CheckSharing
    {
        get => plateMenu.CheckSharing;
        set => plateMenu.CheckSharing = value;
    }

    public void Dispose()
    {
    }

    public override void OnClose()
    {
        // An Advanced open still waiting on the Basic suggestion is dropped (nothing opens, nothing
        // is handled); the next request asks again.
        advancedEntry.Abandon();
        thumbnailTextures.Clear();
        templateThumbnailTextures.Clear();
        cardPreviews.Clear();
    }

    /// <summary>My Plates is always what this window opens to — Manage Templates is a mode
    /// entered from inside a session, never something that persists across reopens.</summary>
    public override void OnOpen()
    {
        activeView = LibraryView.MyPlates;
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        EditorWidgets.SetFirstUseSize(FirstUseSize, MinimumWindowSize);
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void Draw()
    {
        runner.Advance();
        plateMenu.AdvanceOpenGuard();

        if (!library.IsLoaded)
        {
            ImGui.TextDisabled(libraryLoadFailed ? "My Plates couldn't be loaded. See the Dalamud log for details." : "Loading your Plates...");
            return;
        }

        // Templates isn't a permanent top-level tab: My Plates is always what this window opens
        // to. activeView only switches to Templates for as long as the player is inside Manage
        // Templates (entered from the Create Plate chooser), and switches back on its own "Back
        // to My Plates" action or whenever this window is reopened.
        if (activeView == LibraryView.MyPlates)
        {
            DrawMyPlatesView();
        }
        else
        {
            DrawTemplatesView();
        }

        DrawTemplateChooserPopup();
        plateMenu.DrawPopups(characterIdentity.CurrentCharacter);
        DrawBasicGuidancePopup();

        // While the Create Plate chooser is open, its own Rename/Delete requests (from a row's
        // context menu) are drawn from inside the chooser's popup scope instead — see
        // DrawTemplateChooserPopup for why: ImGui.OpenPopup() picks its stack level from how many
        // BeginPopup/EndPopup blocks it's lexically nested inside at the moment it's called, and
        // opening a second popup at the SAME level the chooser occupies closes the chooser first.
        // Calling these two from here too whenever that happens would open Rename/Delete twice in
        // one frame for the same popup id, which is unsafe — so exactly one of the two call sites
        // ever runs on a given frame.
        if (!ImGui.IsPopupOpen(TemplateChooserPopupId))
        {
            DrawTemplateRenamePopup();
            DrawTemplateDeletePopup();
        }

        fileDialogManager.Draw();
    }

    private void DrawMyPlatesView()
    {
        var character = characterIdentity.CurrentCharacter;
        var activePlateId = character is { } who ? library.GetActivePlateId(who.ContentId) : null;
        var plates = library.Search(searchText);
        var allPlates = library.GetOrderedPlates();

        if (selectedPlateId is { } selected && allPlates.All(p => p.PlateId != selected))
        {
            selectedPlateId = null;
        }

        AetherBrand.Header("My Plates");
        DrawHeader(character, allPlates.Count);
        ImGui.Separator();
        if (KeptChanges is { } keptChanges)
        {
            KeptChangesWindow.DrawReminder(keptChanges);
        }

        Help?.DrawReminder();

        // Two plain text lines now (status/info, then the right-click hint) — no button row.
        var footerHeight = (ImGui.GetTextLineHeightWithSpacing() * 2f) + EditorWidgets.Scaled(4f);
        using (var grid = ImRaii.Child("##PlateGrid", new Vector2(-1, -footerHeight), false))
        {
            if (grid.Success)
            {
                TutorialAnchorMarks.MarkWindow(TutorialTarget.LibraryPlateGrid);
                DrawGrid(plates, allPlates.Count, activePlateId, character);
            }
        }

        ImGui.Separator();
        DrawStatusFooter();
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader(CharacterContext? character, int plateCount)
    {
        var headerMin = ImGui.GetCursorScreenPos();
        if (AetherControls.PrimaryButton("Create Plate", tooltip: "Start a new Plate from a Template."))
        {
            pendingTemplateChooserPopup = true;
        }

        TutorialAnchorMarks.Mark(TutorialTarget.LibraryCreatePlate);

        ImGui.SameLine();
        if (ImGui.Button("Import"))
        {
            OpenImportDialog();
        }

        TutorialAnchorMarks.Mark(TutorialTarget.LibraryImport);
        EditorWidgets.Tooltip("Add a Plate from an .aetherframe file. It's checked and previewed first,\nand always added as a new Plate.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(EditorWidgets.Scaled(220f));
        ImGui.InputTextWithHint("##PlateSearch", "Search Plates...", ref searchText, 64);
        TutorialAnchorMarks.Mark(TutorialTarget.LibrarySearch);
        EditorWidgets.Tooltip("Filter the cards by name. Clear it to reorder cards again.");

        if (OpenSharing is { } openSharing)
        {
            ImGui.SameLine();
            if (AetherControls.SecondaryButton("Sharing", tooltip: "Turn sharing on or off for the logged-in character."))
            {
                openSharing();
            }

            TutorialAnchorMarks.Mark(TutorialTarget.LibrarySharing);
        }

        // Never the character's name or World (see MyPlatesCharacterText): only whether one is
        // logged in at all, since Set Active needs one.
        if (MyPlatesCharacterText.HeaderStatus(character) is { } characterStatus)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(characterStatus);
            EditorWidgets.Tooltip("You can still browse, preview, create, and edit Plates.\nLog in to a character to choose its Active Plate.");
        }

        // Help sits in the window's top right corner, with the Plate count just before it.
        var right = ImGui.GetWindowContentRegionMax().X - (Help is null ? 0f : HelpMenu.ButtonWidth + ImGui.GetStyle().ItemSpacing.X);
        if (plateCount > 0)
        {
            var countText = plateCount == 1 ? "1 Plate" : $"{plateCount} Plates";
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), right - ImGui.CalcTextSize(countText).X));
            ImGui.TextDisabled(countText);
        }

        if (Help is { } help)
        {
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - HelpMenu.ButtonWidth));
            help.DrawButton("LibraryHelp", TutorialTarget.LibraryHelp);
        }

        var headerRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        TutorialAnchorMarks.MarkRect(TutorialTarget.LibraryHeader, headerMin, new Vector2(headerRight, ImGui.GetItemRectMax().Y));
    }

    // ---------------------------------------------------------------- card grid

    private void DrawGrid(IReadOnlyList<PlateSummary> plates, int totalCount, Guid? activePlateId, CharacterContext? character)
    {
        if (totalCount == 0)
        {
            DrawEmptyLibrary();
            return;
        }

        if (plates.Count == 0)
        {
            ImGui.TextDisabled("No Plates match your search.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        var available = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((available + spacing.X) / (CardWidth + spacing.X)));
        var canReorder = string.IsNullOrWhiteSpace(searchText);

        listedPlateIds.Clear();
        for (var i = 0; i < plates.Count; i++)
        {
            if (i % columns != 0)
            {
                ImGui.SameLine();
            }

            listedPlateIds.Add(plates[i].PlateId);
            DrawCard(plates[i], plates[i].PlateId == activePlateId, canReorder, character, activePlateId, first: i == 0);
        }

        // Card previews are kept only for the Plates this list shows (deleted or filtered-out ones are dropped).
        cardPreviews.Retain(listedPlateIds);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            dragSourcePlateId = null;
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemHovered())
        {
            selectedPlateId = null;
        }
    }

    private void DrawEmptyLibrary()
    {
        if (AetherControls.EmptyState(
                FontAwesomeIcon.IdCard,
                "You don't have any Plates yet.",
                "A Plate is a complete Adventure Plate style design. Make as many as you like; each character can choose one to be its Active Plate.",
                "Create Your First Plate",
                "Start a new Plate from a Template."))
        {
            pendingTemplateChooserPopup = true;
        }
    }

    private void DrawCard(PlateSummary plate, bool isActive, bool canReorder, CharacterContext? character, Guid? activePlateId, bool first = false)
    {
        if (!cardIds.TryGetValue(plate.PlateId, out var id))
        {
            id = "##PlateCard" + plate.PlateId.ToString("N");
            cardIds[plate.PlateId] = id;
        }

        var thumbnailSize = new Vector2(CardWidth - (CardPadding * 2f), (CardWidth - (CardPadding * 2f)) / ThumbnailAspect);
        var cardSize = new Vector2(CardWidth, thumbnailSize.Y + (CardPadding * 3f) + ImGui.GetTextLineHeight());

        ImGui.InvisibleButton(id, cardSize);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var isSelected = selectedPlateId == plate.PlateId;
        if (first)
        {
            TutorialAnchorMarks.MarkRect(TutorialTarget.LibraryFirstPlateCard, min, max);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            selectedPlateId = plate.PlateId;
        }

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && plate.IsReady)
        {
            RequestEdit(plate.PlateId);
        }

        var contextMenuId = $"##PlateCardMenu{plate.PlateId:N}";
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selectedPlateId = plate.PlateId;
            ImGui.OpenPopup(contextMenuId);
        }

        using (var menu = ImRaii.Popup(contextMenuId))
        {
            if (menu.Success)
            {
                plateMenu.DrawCardItems(plate, character, activePlateId, showInViewer, RequestOpen);
            }
        }

        // A Ready Plate can carry both a note (text that isn't valid) and elements this build can't
        // show; the marker on its thumbnail is explained only here, so neither hides the other.
        if (hovered && CardTooltip(plate.Problem, plate.HasUnsupportedElements) is { } tooltip)
        {
            ImGui.SetTooltip(tooltip);
        }

        if (canReorder)
        {
            DrawCardDragAndDrop(plate, min, max);
        }

        var drawList = ImGui.GetWindowDrawList();
        AetherControls.CardFrame(drawList, min, max, hovered, isSelected);

        var thumbnailMin = min + new Vector2(CardPadding);
        var thumbnailMax = thumbnailMin + thumbnailSize;
        DrawThumbnail(drawList, plate, thumbnailMin, thumbnailMax);

        if (isActive)
        {
            DrawActiveBadge(drawList, thumbnailMin, thumbnailMax);
        }

        // The badge row under Active, or the top row on a Plate that isn't Active.
        var sharingRow = isActive ? 1 : 0;
        if (IsShared?.Invoke(plate.PlateId) == true)
        {
            DrawBadge(drawList, thumbnailMin, thumbnailMax, "Shared", AetherPalette.Info, AetherPalette.TextOnGold, sharingRow);
        }
        else if (IsNotSharedYet?.Invoke(plate.PlateId) == true)
        {
            DrawBadge(drawList, thumbnailMin, thumbnailMax, "Not shared yet", AetherPalette.SurfaceActive, AetherPalette.TextPrimary, sharingRow);
        }

        if (plate.HasUnsupportedElements)
        {
            DrawCompatibilityMarker(drawList, thumbnailMin, thumbnailMax);
        }

        // Name, clipped to the card.
        var textPos = new Vector2(thumbnailMin.X, thumbnailMax.Y + CardPadding);
        drawList.PushClipRect(textPos, new Vector2(thumbnailMax.X, max.Y), true);
        var nameColor = plate.IsReady ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(EditorWidgets.DimTextColor);
        drawList.AddText(textPos, nameColor, plate.DisplayName);
        drawList.PopClipRect();
    }

    private void DrawCardDragAndDrop(PlateSummary plate, Vector2 min, Vector2 max)
    {
        if (ImGui.BeginDragDropSource())
        {
            dragSourcePlateId = plate.PlateId;
            ImGui.SetDragDropPayload(CardDragPayloadType, CardDragPayload, ImGuiCond.None);
            ImGui.TextUnformatted(plate.DisplayName);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            // Left half: drop before this card; right half: after it.
            var placeAfter = ImGui.GetMousePos().X > (min.X + max.X) / 2f;
            var lineX = placeAfter ? max.X + 2f : min.X - 2f;
            ImGui.GetWindowDrawList().AddLine(new Vector2(lineX, min.Y), new Vector2(lineX, max.Y), ImGui.GetColorU32(EditorWidgets.AccentColor), 3f);

            var payload = ImGui.AcceptDragDropPayload(CardDragPayloadType, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (!payload.IsNull && dragSourcePlateId is { } sourceId && sourceId != plate.PlateId)
            {
                var targetId = plate.PlateId;
                runner.Run("reorder", () => library.MovePlateAsync(sourceId, targetId, placeAfter));
                dragSourcePlateId = null;
            }

            ImGui.EndDragDropTarget();
        }
    }

    /// <summary>
    /// The card's preview: a thumbnail image when one is Ready; otherwise the real saved Plate in
    /// miniature, drawn by the shared <see cref="ProfileRenderer"/> (background, portrait, identity,
    /// sections, Components and their overflow — tiny text as soft bars, see <see cref="PlateCardPreview"/>),
    /// fitted to the card and clipped to it; the background-only fallback only when the Plate can't
    /// be loaded. Off-screen cards draw nothing.
    /// </summary>
    private void DrawThumbnail(ImDrawListPtr drawList, PlateSummary plate, Vector2 min, Vector2 max)
    {
        if (plate.IsReady)
        {
            var versionKey = PlateThumbnailService.VersionKeyFor(plate.Revision, plate.ModifiedUtc);
            var thumbnail = thumbnails.Get(plate.PlateId, versionKey, () => library.GetSavedDocument(plate.PlateId));
            if (thumbnailTextures.GetWrapOrNull(plate.PlateId, thumbnail) is { } wrap)
            {
                drawList.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFFu);
                return;
            }

            if (!ImGui.IsRectVisible(min, max))
            {
                return; // scrolled out of view: nothing to draw this frame
            }

            if (cardPreviews.Get(plate.PlateId, versionKey, () => library.GetSavedDocument(plate.PlateId)) is { } preview)
            {
                var fit = PlateCardPreview.Fit(max - min, preview.Bounds);
                if (fit.Scale > 0f)
                {
                    drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);
                    drawList.PushClipRect(min, max, true);
                    ProfileRenderer.Draw(drawList, preview.Document, min + fit.CanvasOffset, fit.Scale, renderResources, PlateCardPreview.Options);
                    drawList.PopClipRect();
                    return;
                }
            }
        }

        DrawFallbackThumbnail(drawList, plate, min, max);
    }

    /// <summary>
    /// A cheap stand-in built only from the saved background's colors (no textures, no text
    /// rendering): enough to tell Plates apart at a glance.
    /// </summary>
    private void DrawFallbackThumbnail(ImDrawListPtr drawList, PlateSummary plate, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(FallbackBackdropColor), 4f);

        var background = plate.IsReady ? library.GetSavedDocument(plate.PlateId)?.Background : null;
        var icon = FontAwesomeIcon.IdCard;

        if (!plate.IsReady)
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

    /// <summary>A card's hover text: its note, the unsupported-elements warning, or both.</summary>
    private static string? CardTooltip(string? problem, bool hasUnsupportedElements) =>
        !hasUnsupportedElements ? problem
        : problem is null ? EditorWidgets.UnsupportedElementsWarning
        : problem + "\n\n" + EditorWidgets.UnsupportedElementsWarning;

    /// <summary>A small warning glyph in the thumbnail's lower-left corner (the card's tooltip explains it).</summary>
    private static void DrawCompatibilityMarker(ImDrawListPtr drawList, Vector2 thumbnailMin, Vector2 thumbnailMax)
    {
        var icon = EditorWidgets.GetIconString(FontAwesomeIcon.ExclamationTriangle);
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var size = ImGui.CalcTextSize(icon);
            var inset = EditorWidgets.Scaled(5f);
            var margin = new Vector2(EditorWidgets.Scaled(3f));
            var pos = new Vector2(thumbnailMin.X + inset, thumbnailMax.Y - size.Y - inset);
            drawList.AddRectFilled(pos - margin, pos + size + margin, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 4f);
            drawList.AddText(pos, ImGui.GetColorU32(EditorWidgets.WarningColor), icon);
        }
    }

    private static void DrawActiveBadge(ImDrawListPtr drawList, Vector2 thumbnailMin, Vector2 thumbnailMax) =>
        DrawBadge(drawList, thumbnailMin, thumbnailMax, "Active", AetherPalette.Gold, AetherPalette.TextOnGold, row: 0);

    /// <summary>A badge in the thumbnail's top right corner, <paramref name="row"/> badges down.</summary>
    private static void DrawBadge(ImDrawListPtr drawList, Vector2 thumbnailMin, Vector2 thumbnailMax, string label, Vector4 background, Vector4 foreground, int row)
    {
        var textSize = ImGui.CalcTextSize(label);
        var padding = EditorWidgets.Scaled(new Vector2(6f, 2f));
        var inset = EditorWidgets.Scaled(4f);
        var height = textSize.Y + (padding.Y * 2f);
        var top = thumbnailMin.Y + inset + (row * (height + inset));
        var badgeMax = new Vector2(thumbnailMax.X - inset, top + height);
        var badgeMin = new Vector2(badgeMax.X - textSize.X - (padding.X * 2f), top);

        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(background), 4f);
        drawList.AddText(badgeMin + padding, ImGui.GetColorU32(foreground), label);
    }
}
