using System;
using System.Numerics;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Theme;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Network;

/// <summary>
/// The small sharing progress window (the owner's request of October 2, 2026): while the logged-in
/// character's Active Plate is shared, after a save, a new Active Plate, or sharing starting or
/// resuming, it says which step it is at, and for how long once that takes a while; then that the
/// Plate is shared, closing by itself, or why it isn't, in the Sharing window's words, with Try
/// again and Close. It follows <see cref="SharingProgress"/>, which reads only what the Sharing
/// window reads, and appears only for a character that shares. It takes no keyboard focus when it
/// appears, is never reached by keyboard or gamepad navigation, makes no sound, is never modal, and
/// sits at the screen's right edge until the player moves it. Compiled only in the networking
/// preview flavour.
/// </summary>
internal sealed class SharingProgressWindow : Window
{
    private const ImGuiWindowFlags ToastFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    /// <summary>The window's width, in unscaled pixels; its height follows what it says.</summary>
    private const float Width = 360f;

    /// <summary>How long a step takes before the window says for how long it has been working.</summary>
    private static readonly TimeSpan ElapsedAfter = TimeSpan.FromSeconds(3);

    private readonly CharacterSharing sharing;
    private readonly LivePublisher live;
    private readonly Func<CharacterContext?> currentCharacter;
    private readonly Func<ulong, Guid?> activePlateOf;
    private readonly SharingProgress progress = new();
    private readonly AetherWindowChrome chrome = new();
    private SharingProgressView view = SharingProgressView.Hidden;
    private bool shown;

    internal SharingProgressWindow(CharacterSharing sharing, LivePublisher live, Func<CharacterContext?> currentCharacter, Func<ulong, Guid?> activePlateOf)
        : base("Sharing progress##AetherFrameSharingProgress", ToastFlags)
    {
        this.sharing = sharing ?? throw new ArgumentNullException(nameof(sharing));
        this.live = live ?? throw new ArgumentNullException(nameof(live));
        this.currentCharacter = currentCharacter ?? throw new ArgumentNullException(nameof(currentCharacter));
        this.activePlateOf = activePlateOf ?? throw new ArgumentNullException(nameof(activePlateOf));
        RespectCloseHotkey = true;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    /// <summary>Opens the Sharing window, for a share only it can help with.</summary>
    internal Action? OpenSharing { get; set; }

    /// <summary>
    /// Every frame, open or not, before Dalamud looks at whether it is open: the window follows
    /// sharing, and opens and closes with it. Closed by the player since the last frame (Close,
    /// Hide, or Escape while it has focus), what it showed goes, and a share still under way stays
    /// out of sight until it ends.
    /// </summary>
    public override void PreOpenCheck()
    {
        if (shown && !IsOpen)
        {
            progress.Dismiss();
        }

        var character = currentCharacter()?.ContentId;
        view = progress.Update(character, sharing.View, live.View, TimeSpan.FromMilliseconds(Environment.TickCount64), character is { } id ? activePlateOf(id) : null);
        shown = view.Visible;
        IsOpen = shown;
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport();
        var anchor = new Vector2(viewport.WorkPos.X + viewport.WorkSize.X - (AetherMetrics.SpaceXl * scale), viewport.WorkPos.Y + (viewport.WorkSize.Y * 0.7f));
        ImGui.SetNextWindowPos(anchor, ImGuiCond.FirstUseEver, new Vector2(1f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Width * scale, 0f), ImGuiCond.Always);
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void Draw()
    {
        AetherControls.SectionHeader(SharingText.ProgressTitle, 0f);
        switch (view.Stage)
        {
            case SharingProgressStage.Shared:
                AetherControls.Callout(AetherTone.Success, view.Message);
                break;
            case SharingProgressStage.Problem:
                DrawProblem();
                break;
            case SharingProgressStage.Hidden:
                break;
            default:
                DrawWorking();
                break;
        }
    }

    /// <summary>A working status: the info icon, then the step in the info tone, wrapped to the window.</summary>
    private static void Status(string text)
    {
        var (color, _, icon) = AetherControls.Of(AetherTone.Info);
        EditorWidgets.IconText(icon, color);
        ImGui.SameLine(0f, AetherMetrics.ItemInnerSpacing * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.TextUnformatted(text);
        }
    }

    private void DrawWorking()
    {
        Status(view.Message);
        if (view.Elapsed >= ElapsedAfter)
        {
            AetherControls.Muted(SharingText.Elapsed(view.Elapsed));
        }

        if (view.Stage != SharingProgressStage.Sending)
        {
            return;
        }

        AetherControls.Muted(SharingText.SendingTakesTime);
        if (AetherControls.SecondaryButton("Stop sending"))
        {
            sharing.StopSending();
        }

        ImGui.SameLine();
        if (AetherControls.GhostButton("Hide", tooltip: "Hide this window. Sending goes on, and the Sharing window says how it went."))
        {
            IsOpen = false;
        }
    }

    private void DrawProblem()
    {
        AetherControls.Callout(AetherTone.Warning, view.Message);
        if (view.Problems.Count > 0)
        {
            CandidateView.DrawProblems(view.Problems);
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceXs * ImGuiHelpers.GlobalScale));
        switch (view.Action)
        {
            case SharingProgressAction.SendAgain:
                // A newer build under way replaces the waiting revision: nothing to send again then.
                using (ImRaii.Disabled(sharing.View.Busy || (live.View.ContentId == view.ContentId && live.View.Building)))
                {
                    if (AetherControls.PrimaryButton("Try again"))
                    {
                        sharing.TrySendWaiting(view.ContentId);
                    }
                }

                ImGui.SameLine();
                break;
            case SharingProgressAction.ShareAgain:
                if (AetherControls.PrimaryButton("Try again"))
                {
                    live.Retry();
                }

                ImGui.SameLine();
                break;
            case SharingProgressAction.OpenSharing when OpenSharing is { } open:
                if (AetherControls.PrimaryButton("Open Sharing"))
                {
                    open();
                    IsOpen = false;
                }

                ImGui.SameLine();
                break;
        }

        if (AetherControls.SecondaryButton("Close"))
        {
            IsOpen = false;
        }
    }
}
