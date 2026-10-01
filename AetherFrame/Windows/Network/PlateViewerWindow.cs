using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The viewer (NETWORK2's N2-10; decisions V1, V5, C5 and D6): a search by full name and World, and
/// another player's Plate drawn read-only, as it draws for them, with refreshing, hiding and
/// reporting. It is opened from the game's right-click menu on a character, from this search, or
/// from the Sharing window. It shows nothing and looks nothing up until one of the player's
/// characters shares. A name, a World or a Plate's name is only ever drawn unformatted, never inside
/// an ImGui label, a tooltip or a format string (N7). Nothing shown is kept once the window closes.
/// Compiled only in the networking preview flavour.
/// </summary>
internal sealed class PlateViewerWindow : Window, IDisposable
{
    internal const string NotSharing = "Viewing other players' Plates is part of sharing. Turn sharing on for one of your characters first, in My Plates' Sharing window: then you can view other players who share too, from the game's right-click menu on their character or by searching for them here.";
    internal const string Address = "Looking a Plate up sends your network address to AetherFrame's server, which doesn't store it, and keeps no record of who viewed whom.";

    private const int NameBytes = 64;

    private readonly PlateViewing viewing;
    private readonly ITextureProvider textures;
    private readonly ProfileRenderResources resources;
    private readonly Func<IReadOnlyList<string>> worlds;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly AetherWindowChrome chrome = new();
    private readonly Func<int, IDalamudTextureWrap?> imageOf;
    private string name = "";
    private string world = "";
    private PlateTarget? seenTarget;
    private Textures? shown;
    private string? actionProblem;

    internal PlateViewerWindow(PlateViewing viewing, ITextureProvider textures, ProfileRenderResources resources, Func<IReadOnlyList<string>> worlds, Func<CharacterContext?> currentCharacter)
        : base("AetherFrame Plates##AetherFramePlateViewer", ImGuiWindowFlags.NoCollapse)
    {
        this.viewing = viewing;
        this.textures = textures;
        this.resources = resources;
        this.worlds = worlds;
        this.currentCharacter = currentCharacter;
        imageOf = index => shown?[index];
        Size = new Vector2(640f, 640f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 420f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = true;
    }

    /// <summary>Opens the window on its search, or on the Plate just asked for.</summary>
    internal void Open()
    {
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void OnClose()
    {
        viewing.Close();
        Release();
    }

    public void Dispose() => Release();

    public override void Draw()
    {
        if (!viewing.CanView)
        {
            Wrapped(NotSharing, AetherPalette.TextMuted);
            return;
        }

        var view = viewing.View;
        if (view.Target is { } target && target != seenTarget)
        {
            // A Plate asked for from the game's menu fills the search in, so it can be refreshed or changed.
            seenTarget = target;
            name = target.Name;
            world = target.World;
            actionProblem = null;
        }

        DrawSearch();
        AetherControls.Divider();

        if (!ReferenceEquals(shown?.Plate, view.Plate))
        {
            Release();
            if (view.Plate is { } plate)
            {
                shown = new Textures(textures, plate);
            }
        }

        if (actionProblem is not null)
        {
            AetherControls.Callout(AetherTone.Warning, actionProblem);
        }

        switch (view.Stage)
        {
            case ViewStage.Idle:
                Wrapped("Search for a player by their character's full name and World, or right-click their character in game and choose View AetherFrame Plate.", AetherPalette.TextMuted);
                break;

            case ViewStage.Waiting:
            case ViewStage.Looking:
                AetherControls.StatusLine(AetherTone.Info, "Looking it up...");
                break;

            case ViewStage.NotFound:
                Wrapped("That character has no AetherFrame Plate to show: they may not share, or may have paused or turned sharing off.");
                break;

            case ViewStage.Hidden:
                Wrapped("You hid this player's Plate on this PC, so it isn't looked up.");
                if (AetherControls.SecondaryButton("Show their Plate again##AetherFrameViewerUnhide") && !viewing.Unhide())
                {
                    actionProblem = HideProblem();
                }

                break;

            case ViewStage.Failed:
                AetherControls.Callout(AetherTone.Warning, FailureText(view.Failure));
                if (view.Failure != ViewFailure.NotSharing && AetherControls.SecondaryButton("Try again##AetherFrameViewerRetry"))
                {
                    viewing.Refresh();
                }

                break;

            case ViewStage.Shown when view.Plate is { } plate && view.Target is { } viewed:
                DrawPlate(view, plate, viewed);
                break;
        }

        ImGui.Spacing();
        Wrapped(Address, AetherPalette.TextMuted);
    }

    private static string FailureText(ViewFailure failure) => failure switch
    {
        ViewFailure.NotSharing => NotSharing,
        ViewFailure.TooMany => "You've looked up a lot of Plates in a short time. Try again in a little while.",
        ViewFailure.Unreachable => "AetherFrame's server couldn't be reached. Check your connection, then try again.",
        ViewFailure.KeyUnavailable => "Your shared character's key couldn't be opened on this PC. My Plates' Sharing window says why.",
        ViewFailure.TakenOver => "Another AetherFrame's Lodestone check took over your shared character, so it can't look Plates up from here. My Plates' Sharing window says what to do.",
        _ => "The server refused the lookup, or sent something AetherFrame doesn't show.",
    };

    private static void Wrapped(string text, Vector4? color = null)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawSearch()
    {
        if (world.Length == 0 && currentCharacter() is { HomeWorld: { } home })
        {
            world = home;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var viewWidth = ImGui.CalcTextSize("View").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var worldWidth = EditorWidgets.Scaled(150f);
        var nameWidth = MathF.Max(EditorWidgets.Scaled(120f), ImGui.GetContentRegionAvail().X - worldWidth - viewWidth - (spacing * 2f));

        ImGui.SetNextItemWidth(nameWidth);
        var entered = ImGui.InputTextWithHint("##AetherFrameViewerName", "Character name, e.g. Aria Starfall", ref name, NameBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(worldWidth);
        using (var combo = ImRaii.Combo("##AetherFrameViewerWorld", world.Length == 0 ? "World" : world))
        {
            if (combo.Success)
            {
                foreach (var option in worlds())
                {
                    if (ImGui.Selectable(option, string.Equals(option, world, StringComparison.Ordinal)))
                    {
                        world = option;
                    }
                }
            }
        }

        ImGui.SameLine();
        var valid = PlateViewing.IsName(name) && PlateViewing.IsWorld(world);
        using (ImRaii.Disabled(!valid))
        {
            if ((AetherControls.PrimaryButton("View##AetherFrameViewerSearch") || (entered && valid)) && valid)
            {
                viewing.Open(name, world);
            }
        }
    }

    private void DrawPlate(PlateViewerView view, ViewedPlate viewed, PlateTarget target)
    {
        var plate = viewed.Plate;
        using (AetherFonts.Heading())
        {
            ImGui.TextUnformatted(target.Name);
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted(target.World);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextMuted))
        {
            ImGui.TextUnformatted(plate.Name);
        }

        var notes = new List<string>(plate.Notes);
        if (viewed.ImagesRefused > 0)
        {
            notes.Add(viewed.ImagesRefused.ToString(CultureInfo.InvariantCulture) + (viewed.ImagesRefused == 1 ? " image didn't pass AetherFrame's checks, so it isn't shown." : " images didn't pass AetherFrame's checks, so they aren't shown."));
        }

        if (viewed.ImagesMissing > 0)
        {
            notes.Add(viewed.ImagesMissing.ToString(CultureInfo.InvariantCulture) + (viewed.ImagesMissing == 1 ? " image" : " images") + " couldn't be loaded: the Plate may have changed while it loaded. Refresh to see it as it is now.");
        }

        // The Plate takes what the actions, the notes and the address line leave.
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var reserved = ImGui.GetFrameHeightWithSpacing() + (lineHeight * (notes.Count + 3)) + ImGui.GetStyle().ItemSpacing.Y;
        var available = ImGui.GetContentRegionAvail();
        var area = new Vector2(available.X, MathF.Max(EditorWidgets.Scaled(120f), available.Y - reserved));
        var scale = MathF.Min(area.X / plate.Canvas.X, area.Y / plate.Canvas.Y);
        var drawn = plate.Canvas * scale;
        var topLeft = ImGui.GetCursorScreenPos();
        var origin = topLeft + new Vector2((area.X - drawn.X) / 2f, (area.Y - drawn.Y) / 2f);
        ServedPlatePainter.Draw(ImGui.GetWindowDrawList(), plate, origin, scale, topLeft, topLeft + area, resources, imageOf);
        ImGui.Dummy(area);

        foreach (var note in notes)
        {
            Wrapped(note, AetherPalette.TextMuted);
        }

        if (AetherControls.SecondaryButton("Refresh##AetherFrameViewerRefresh", tooltip: "Look this Plate up again"))
        {
            viewing.Refresh();
        }

        ImGui.SameLine();
        if (AetherControls.SecondaryButton("Hide this player##AetherFrameViewerHide", tooltip: "Never look this player's Plate up on this PC, until you show it again. Nothing is sent.") && !viewing.Hide())
        {
            actionProblem = HideProblem();
        }

        ImGui.SameLine();
        switch (view.Report)
        {
            case ReportStage.Sending:
                AetherControls.MutedInline("Sending the report...");
                break;

            case ReportStage.Sent:
                AetherControls.MutedInline("Reported. Thank you.");
                break;

            default:
                if (AetherControls.SecondaryButton("Report...##AetherFrameViewerReport", tooltip: "Tell AetherFrame's operator about this Plate"))
                {
                    ImGui.OpenPopup("##AetherFrameViewerReportPopup");
                }

                if (view.Report == ReportStage.Failed)
                {
                    ImGui.SameLine();
                    AetherControls.MutedInline("The report couldn't be sent.");
                }

                break;
        }

        using var popup = ImRaii.Popup("##AetherFrameViewerReportPopup");
        if (popup.Success)
        {
            ImGui.TextUnformatted("Report this Plate as:");
            foreach (var (reason, label) in new[] { ("offensive", "Offensive"), ("impersonation", "Pretending to be someone else"), ("spam", "Spam or advertising"), ("other", "Something else") })
            {
                if (ImGui.Selectable(label))
                {
                    viewing.Report(reason);
                }
            }
        }
    }

    private string HideProblem() => viewing.Hidden.Unreadable
        ? "AetherFrame's list of hidden players (" + HiddenPlates.FileName + " in its configuration folder) can't be read, so nobody can be hidden or shown again until it's fixed or moved aside."
        : "AetherFrame couldn't save its list of hidden players, so nothing changed. Try again.";

    private void Release()
    {
        if (shown is { } released)
        {
            ServedPlatePainter.Forget(released.Plate.Plate);
            released.Dispose();
        }

        shown = null;
    }

    /// <summary>The received images of one Plate, decoded for display; each is drawn once it has loaded, and none after <see cref="Dispose"/>.</summary>
    private sealed class Textures : IDisposable
    {
        private readonly IDalamudTextureWrap?[] wraps;
        private bool disposed;

        internal Textures(ITextureProvider provider, ViewedPlate plate)
        {
            Plate = plate;
            wraps = new IDalamudTextureWrap?[plate.Images.Count];
            for (var index = 0; index < wraps.Length; index++)
            {
                if (plate.Images[index] is { } bytes)
                {
                    _ = LoadAsync(provider, bytes, index);
                }
            }
        }

        internal ViewedPlate Plate { get; }

        internal IDalamudTextureWrap? this[int index]
        {
            get
            {
                lock (wraps)
                {
                    return index >= 0 && index < wraps.Length ? wraps[index] : null;
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

        private async Task LoadAsync(ITextureProvider provider, byte[] bytes, int index)
        {
            try
            {
                var wrap = await provider.CreateFromImageAsync(bytes, "AetherFrame viewer").ConfigureAwait(false);
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
                // Checked before it got here; one the game can't decode just isn't drawn.
            }
        }
    }
}
