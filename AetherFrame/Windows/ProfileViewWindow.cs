using System;
using System.Numerics;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Rendering;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using AetherFrame.Windows.Tutorial;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The Plate Viewer: a read-only presentation of one Plate — no inspector, no selection, no
/// editing of any kind, no editor chrome of any kind; the finished Plate is the entire point of
/// this window. For an explicitly requested Plate that's open in the editors it renders that same
/// live in-memory <see cref="ProfileDocument"/> (so it reflects unsaved edits without a reload); for
/// any other Plate, and always for the default Active Plate request, the Library's saved copy; for
/// a Template preview, the Template's document. Either way via
/// the shared <see cref="ProfileRenderer"/> with element-bounds chrome always off. Viewing never
/// changes a Plate, its dirty state, its undo history, or which Plate is Active.
///
/// <para><b>What it shows</b> (<see cref="PlateViewerTarget"/>): an explicitly requested Plate or
/// Template document, or a Plate that isn't a document here at all (<see cref="IPlatePresentation"/>,
/// such as another player's shared Plate), presented the same way; otherwise — the default request, e.g. <c>/aetherframe view</c> — the
/// logged-in character's Active Plate, always as last saved (never the editors' unsaved state). With
/// no Active Plate it shows an intentional empty state pointing at My Plates, never some other Plate.</para>
///
/// <para><b>Presentation.</b> The Plate floats directly over the game: the window is exactly the
/// Plate's composition (its fitted visual bounds — canvas plus any intentional Component overflow)
/// and draws nothing of its own (<see cref="PlateViewerPresentation"/>).
/// The Plate's own background is drawn as authored. The only chrome is an always-visible Close
/// control; only the composition and that control take mouse input.</para>
///
/// <para><b>Interaction.</b> Left-drag anywhere on the composition moves the viewer (Close takes
/// priority). Ctrl + mouse wheel over it resizes it in steps, around the Plate's center (a plain
/// wheel doesn't). Right-click opens a small menu: size presets, Reset Size, Center on Screen. A
/// short hint explains this once per session. Position and size (<see cref="PlateViewerPlacement"/>)
/// are session UI state — kept when the viewer reopens or shows another Plate — never Plate data.</para>
/// </summary>
internal sealed class ProfileViewWindow : Window, IDisposable
{
    /// <summary>The transparent presentation's window flags: no title bar, background, native
    /// resize/move or scrolling — placed and sized by <see cref="PlateViewerPlacement"/> every frame.</summary>
    internal const ImGuiWindowFlags PresentationFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse;

    private const ImGuiWindowFlags MessageFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize;

    private const string ContextMenuId = "##PlateViewerMenu";

    private readonly ProfileService profileService;
    private readonly PlateLibraryService library;
    private readonly ActivePlateResolver activePlates;
    private readonly ProfileRenderResources renderResources;

    // The artwork the shown Plate is missing, the player's own or another player's: downloaded as
    // the Plate is viewed (art on demand).
    private readonly ArtNeeds viewerArt = new();
    private readonly Action openMyPlates;

    // Where and how large the viewer shows its Plate: session UI state, kept across reopening and Plates.
    private readonly PlateViewerPlacement placement = new();
    private readonly PlateViewerHint hint = new();

    // What the viewer was last asked to show; starts as the default (Active Plate) request.
    private readonly PlateViewerTarget target = new();

    // This frame's resolved content and presentation (computed in PreDraw, used by Draw, style
    // pushes popped in PostDraw), and the style's own window padding from before the presentation
    // zeroed it, for the context menu.
    private bool presenting;
    private PlateViewerContent content;
    private ProfileDocument? presentedDocument;
    private CanvasBounds presentedBounds;
    private Vector2 presentedCanvasSize;
    private PlateViewerLayout? layout;

    // A Plate that isn't a document here, presented in place of the target while set.
    private IPlatePresentation? presentation;
    private Vector2 styleWindowPadding;

    /// <param name="openMyPlates">The No Active Plate empty state's Open My Plates action.</param>
    internal ProfileViewWindow(
        ProfileService profileService, PlateLibraryService library, ActivePlateResolver activePlates, ProfileRenderResources renderResources, Action openMyPlates)
        : base("AetherFrame Plate Viewer##ProfileViewWindow")
    {
        this.profileService = profileService;
        this.library = library;
        this.activePlates = activePlates;
        this.renderResources = renderResources;
        this.openMyPlates = openMyPlates;
    }

    /// <summary>
    /// The default viewing request: shows the logged-in character's Active Plate — whichever it is
    /// while the viewer stays open — or the No Active Plate empty state.
    /// </summary>
    internal void ShowActivePlate()
    {
        ReleasePresentation();
        target.RequestActivePlate();
        IsOpen = true;
        BringToFront();
    }

    /// <summary>Shows a specific Plate (its live copy if it's the one open in the editors).</summary>
    internal void ShowPlate(Guid plateId)
    {
        ReleasePresentation();
        target.RequestPlate(plateId);
        IsOpen = true;
        BringToFront();
    }

    /// <summary>
    /// Shows a document that isn't a saved Plate — a Template's saved or freshly generated
    /// content. Read-only, exactly like viewing a Plate: never mutates <paramref name="document"/>,
    /// never changes which Plate is Active, and never touches the editors' own state.
    /// </summary>
    internal void ShowDocument(ProfileDocument document)
    {
        ReleasePresentation();
        target.RequestDocument(document);
        IsOpen = true;
    }

    /// <summary>
    /// Presents a Plate that isn't a document here (another player's, say), exactly as a Plate is
    /// presented, until the viewer closes or is asked to show something else.
    /// </summary>
    internal void ShowPresentation(IPlatePresentation shown)
    {
        if (!ReferenceEquals(presentation, shown))
        {
            ReleasePresentation();
            presentation = shown;
        }

        IsOpen = true;
        BringToFront();
    }

    public void Dispose() => ReleasePresentation();

    private void ReleasePresentation()
    {
        if (presentation is { } released)
        {
            presentation = null;
            released.Released();
        }
    }

    // AetherFrame's style around this window's frame, and the tutorial's window policy.
    private readonly AetherWindowChrome chrome = new();

    // Escape on the right-click menu closes only the menu, never the viewer.
    private readonly PopupEscapeGuard escape = new();

    public override void OnClose()
    {
        placement.EndDrag();
        ReleasePresentation();
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        presenting = false;
        layout = null;
        var viewport = ImGui.GetMainViewport();
        if (presentation is { } shown)
        {
            presentedDocument = null;
            if (shown.TryGetBounds(out var shownBounds, out var shownCanvas) && TryPresent(shownBounds, shownCanvas, viewport))
            {
                return;
            }
        }
        else
        {
            content = target.Resolve(activePlates, library.GetSavedDocument, profileService.CurrentProfile);
            presentedDocument = content.Document;
            if (presentedDocument is { } document && TryPresent(ProfileVisualBounds.Compute(document), CanvasSize(document), viewport))
            {
                return;
            }
        }

        // Nothing to present: a small ordinary message window, centered, with its own Close.
        placement.EndDrag();
        ImGui.SetNextWindowPos(viewport.WorkPos + (viewport.WorkSize / 2f), ImGuiCond.Always, new Vector2(0.5f));
        Flags = MessageFlags;
        AllowBackgroundBlur = true;
        AetherWindowChrome.ApplyPolicy(this);
    }

    /// <summary>Sets the frame up to present <paramref name="bounds"/>, floating over the game; false when there's nothing to fit.</summary>
    private bool TryPresent(CanvasBounds bounds, Vector2 canvasSize, ImGuiViewportPtr viewport)
    {
        if (placement.Update(bounds, canvasSize, viewport.WorkPos, viewport.WorkSize, ControlSize) is { } computed)
        {
            layout = computed;
            presentedBounds = bounds;
            presentedCanvasSize = canvasSize;
            hint.ShowOnce(ImGui.GetTime());
            styleWindowPadding = ImGui.GetStyle().WindowPadding;
            ImGui.SetNextWindowPos(computed.WindowPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(computed.WindowSize, ImGuiCond.Always);
            Flags = PresentationFlags;
            AllowBackgroundBlur = PlateViewerPresentation.AllowBackgroundBlur;

            // Popped in PostDraw (Dalamud calls it after End on every frame PreDraw ran).
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, PlateViewerPresentation.WindowPadding);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, PlateViewerPresentation.WindowBorderSize);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, PlateViewerPresentation.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, PlateViewerPresentation.BackgroundColor);
            presenting = true;
            AetherWindowChrome.ApplyPolicy(this);
            return true;
        }

        return false;
    }

    public override void PostDraw()
    {
        if (presenting)
        {
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(2);
            presenting = false;
        }

        chrome.PopStyle();
    }

    public override void Draw()
    {
        using var popupEscape = escape.Update(this);
        if (presenting && layout is { } current && (presentation is not null || presentedDocument is not null))
        {
            DrawPresentation(current);
            return;
        }

        if (presentation is { } shown)
        {
            DrawPresentationMessage(shown);
            return;
        }

        if (content.State == PlateViewerState.NoActivePlate)
        {
            DrawNoActivePlate();
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(content.State switch
        {
            PlateViewerState.Showing => "This Plate can't be shown.",
            PlateViewerState.NoCharacter => "Log in to a character to view its Active Plate.",
            PlateViewerState.LibraryUnavailable => "My Plates isn't available right now.",
            _ => "This Plate isn't available.",
        });
        ImGui.SameLine();
        DrawMessageClose();
    }

    /// <summary>The default request's intentional empty state: this character has no Active Plate.</summary>
    private void DrawNoActivePlate()
    {
        // Heading with Close at the right edge of the fixed-width explanation below it.
        const string heading = "No Active Plate";
        var left = ImGui.GetCursorPosX();
        var width = 300f * ImGuiHelpers.GlobalScale;
        var headingEnd = left + ImGui.CalcTextSize(heading).X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(heading);
        ImGui.SameLine(Math.Max(headingEnd, left + width - ImGui.GetFrameHeight()));
        DrawMessageClose();

        using (ImRaii.TextWrapPos(left + width))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextUnformatted("Choose an Active Plate in My Plates to make it your default AetherFrame Plate.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Open My Plates"))
        {
            // Choosing one there is enough: the viewer never reopens on its own because Active changed.
            IsOpen = false;
            openMyPlates();
        }
    }

    /// <summary>What a presented Plate says while it has nothing to draw: its message, wrapped, with Close at the right and its action, if any, under it.</summary>
    private void DrawPresentationMessage(IPlatePresentation shown)
    {
        var left = ImGui.GetCursorPosX();
        var width = 340f * ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorPosY();
        using (ImRaii.TextWrapPos(left + width))
        {
            ImGui.TextUnformatted(shown.Message);
        }

        var end = ImGui.GetCursorPosY();
        ImGui.SetCursorPos(new Vector2(left + width + ImGui.GetStyle().ItemSpacing.X, start));
        DrawMessageClose();
        ImGui.SetCursorPosY(Math.Max(end, ImGui.GetCursorPosY()));
        if (shown.MessageAction is { } action)
        {
            ImGui.Spacing();
            if (ImGui.Button(action))
            {
                shown.RunMessageAction();
            }
        }
    }

    private void DrawMessageClose()
    {
        if (PresentationControls.Close("##CloseProfileView", ImGui.GetCursorScreenPos(), ImGui.GetFrameHeight(), "Close"))
        {
            IsOpen = false;
        }
    }

    private static float ControlSize => PlateViewerLayout.DefaultControlSize * ImGuiHelpers.GlobalScale;

    private static Vector2 CanvasSize(ProfileDocument document) => new(Math.Max(0f, document.CanvasWidth), Math.Max(0f, document.CanvasHeight));

    private void DrawPresentation(PlateViewerLayout current)
    {
        var windowPos = ImGui.GetWindowPos();
        var mouse = ImGui.GetMousePos();
        var io = ImGui.GetIO();
        var viewport = ImGui.GetMainViewport();
        var hovered = ImGui.IsWindowHovered();
        var region = hovered ? current.HitTest(mouse) : PlateViewerRegion.None;

        // Move: a left press on the composition (never on Close) moves the whole viewer until release.
        if (!placement.IsDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && placement.TryBeginDrag(region, mouse))
        {
            hint.Dismiss();
        }

        if (placement.IsDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                placement.DragTo(mouse);
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
            else
            {
                placement.EndDrag();
            }
        }

        // Resize: Ctrl + wheel only (a plain wheel does nothing here).
        if (hovered && io.KeyCtrl && io.MouseWheel != 0f && placement.ZoomByWheel(io.MouseWheel, presentedBounds, presentedCanvasSize, viewport.WorkSize, current.ControlSize))
        {
            hint.Dismiss();
        }

        // Options: right-click anywhere on the viewer (never starts a move: moves are left-button only).
        if (region != PlateViewerRegion.None && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            hint.Dismiss();
            ImGui.OpenPopup(ContextMenuId);
        }

        viewerArt.Begin(renderResources);
        if (presentation is { } shown)
        {
            shown.Draw(ImGui.GetWindowDrawList(), windowPos + current.CanvasOffset, current.Scale, windowPos, windowPos + current.WindowSize);

            // Another player's Plate (the sharing build's): the tutorial explains its right-click menu.
            TutorialAnchorMarks.MarkRect(TutorialTarget.ViewerOtherPlayersPlate, windowPos, windowPos + current.WindowSize);
        }
        else if (presentedDocument is { } profile)
        {
            ProfileRenderer.Draw(ImGui.GetWindowDrawList(), profile, windowPos + current.CanvasOffset, current.Scale, renderResources, PlateViewerPresentation.RenderOptions);
        }

        viewerArt.End(renderResources);

        // Before Close, so Close always paints over it.
        DrawArtStatus(windowPos, current);

        if (PresentationControls.Close("##ViewerClose", windowPos + current.CloseOffset, current.ControlSize, "Close (Esc)"))
        {
            placement.EndDrag();
            IsOpen = false;
        }

        DrawContextMenu(current);
        DrawHint(windowPos, current);
    }

    private void DrawContextMenu(PlateViewerLayout current)
    {
        // The presentation zeroed WindowPadding for the viewer itself; the menu gets the normal padding.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, styleWindowPadding);
        try
        {
            if (!ImGui.BeginPopup(ContextMenuId))
            {
                return;
            }

            var viewport = ImGui.GetMainViewport();
            var canvasSize = presentedCanvasSize;
            if (presentation is { } shown)
            {
                shown.DrawMenuItems();
                ImGui.Separator();
            }

            var art = viewerArt.Summary(renderResources.ArtStore);
            if (art.Kind == ArtNeedKind.Failed)
            {
                ImGui.TextDisabled(art.Label);
                if (ImGui.MenuItem("Try Downloading the Artwork Again"))
                {
                    viewerArt.TryAgain(renderResources.ArtStore);
                }

                ImGui.Separator();
            }

            ImGui.TextDisabled($"Size: {placement.Percent}%");
            ImGui.Separator();
            foreach (var percent in PlateViewerPlacement.PresetPercents)
            {
                if (ImGui.MenuItem($"{percent}%", string.Empty, placement.Percent == percent))
                {
                    placement.SetPercent(percent, presentedBounds, canvasSize, viewport.WorkSize, current.ControlSize);
                }
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Reset Size"))
            {
                placement.ResetSize(presentedBounds, canvasSize, viewport.WorkSize, current.ControlSize);
            }

            if (ImGui.MenuItem("Center on Screen"))
            {
                placement.CenterOnScreen(presentedBounds, viewport.WorkPos, viewport.WorkSize);
            }

            ImGui.EndPopup();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>
    /// What the shown Plate's missing artwork is doing (art on demand): a pill at the composition's top
    /// center, in the viewer's own window (so other windows cover it), while it downloads or after it
    /// failed (Try again is in the right-click menu). Nothing while nothing is missing.
    /// </summary>
    private void DrawArtStatus(Vector2 windowPos, PlateViewerLayout current)
    {
        var summary = viewerArt.Summary(renderResources.ArtStore);
        if (summary.Kind == ArtNeedKind.None)
        {
            return;
        }

        var text = summary.Kind == ArtNeedKind.Failed ? "Artwork didn't download. Right-click to try again." : summary.Label;
        var padding = new Vector2(10f, 5f) * ImGuiHelpers.GlobalScale;
        var room = current.WindowSize.X - (2f * (current.ControlSize + (16f * ImGuiHelpers.GlobalScale)));
        if (ImGui.CalcTextSize(text).X + (padding.X * 2f) > room)
        {
            // A narrow viewer: the short form, clear of the Close control.
            text = summary.Kind == ArtNeedKind.Failed ? "Artwork failed: right-click"
                : summary.Total > 0 ? $"{ArtNeedSummary.Megabytes(summary.Received)} / {ArtNeedSummary.Megabytes(summary.Total)} MB"
                : "Downloading artwork";
        }

        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + (padding * 2f);
        var min = windowPos + new Vector2((current.WindowSize.X - size.X) / 2f, 12f * ImGuiHelpers.GlobalScale);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(PlateViewerPresentation.HintBacking), size.Y / 2f);
        drawList.AddText(min + padding, ImGui.GetColorU32(summary.Kind == ArtNeedKind.Failed ? AetherPalette.Warning : PlateViewerPresentation.HintText), text);
    }

    /// <summary>The once-per-session usage hint: a small pill at the composition's bottom center, on
    /// the foreground layer (so it neither takes input nor gets clipped), fading out after a few seconds.</summary>
    private void DrawHint(Vector2 windowPos, PlateViewerLayout current)
    {
        var opacity = hint.Opacity(ImGui.GetTime());
        if (opacity <= 0f)
        {
            return;
        }

        var textSize = ImGui.CalcTextSize(PlateViewerHint.Text);
        var padding = new Vector2(10f, 5f) * ImGuiHelpers.GlobalScale;
        var size = textSize + (padding * 2f);
        var bottomCenter = windowPos + new Vector2(current.WindowSize.X / 2f, current.WindowSize.Y);
        var min = bottomCenter - new Vector2(size.X / 2f, size.Y + (12f * ImGuiHelpers.GlobalScale));

        var drawList = ImGui.GetForegroundDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(PlateViewerPresentation.HintBacking with { W = PlateViewerPresentation.HintBacking.W * opacity }), size.Y / 2f);
        drawList.AddText(min + padding, ImGui.GetColorU32(PlateViewerPresentation.HintText with { W = PlateViewerPresentation.HintText.W * opacity }), PlateViewerHint.Text);
    }
}
