using System;
using System.Numerics;
using AetherFrame.UI.Theme;
using AetherFrame.UI.Tutorial;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows.Tutorial;

/// <summary>
/// The welcome for a player new to AetherFrame: Create My First Plate (guided creation), Not Now, or
/// Don't Show Again, in a small centered window. Shown once the Libraries have loaded, only for a
/// new install with no Plate (see <see cref="GuidedCreation.ResolveWelcome"/>), and only once a
/// character is logged in and no recovery offer waits for an answer
/// (<see cref="GuidedCreation.WelcomeMayShow"/>): kept unsaved changes always come first. Not Now,
/// or closing it, asks again on a later load while there is still no Plate, a few times at most.
/// The full tutorial is mentioned, not offered: it waits under Help as reference.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const ImGuiWindowFlags PromptFlags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;

    private readonly GuidedCreation guided;
    private readonly Func<bool> mayShow;
    private readonly AetherWindowChrome chrome = new();
    private bool creating;
    private string? startError;

    /// <param name="guided">Guided creation, which decides whether to welcome and starts it.</param>
    /// <param name="mayShow">Whether nothing about recovery stands in front of the welcome (<see cref="GuidedCreation.WelcomeMayShow"/>).</param>
    internal WelcomeWindow(GuidedCreation guided, Func<bool> mayShow)
        : base("Welcome to AetherFrame##AetherFrameFirstRun", PromptFlags, forceMainWindow: true)
    {
        this.guided = guided;
        this.mayShow = mayShow;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    /// <summary>
    /// Opens when a welcome waits and nothing stands in front of it; closes once the Plate it started
    /// is made and open. A start that made nothing (the unsaved-changes question's Cancel, or a
    /// failure, which it shows) leaves the welcome open to answer again.
    /// </summary>
    public override void PreOpenCheck()
    {
        if (!IsOpen && guided.WelcomeRequested && guided.ConsumeWelcome(mayShow()))
        {
            creating = false;
            startError = null;
            IsOpen = true;
            BringToFront();
        }

        if (IsOpen && creating && !guided.IsStarting)
        {
            creating = false;
            startError = guided.StartError;
            if (startError is null && guided.CanContinue)
            {
                // The Plate is made and open in the Basic editor, on its first step.
                IsOpen = false;
            }
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

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 440f * scale;

        AetherBrand.Header("Welcome to AetherFrame", "Plates for your character");
        ImGui.Dummy(new Vector2(width, AetherMetrics.SpaceSm * scale));

        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            ImGui.TextWrapped("Make your first Plate, a character card in the style of the Adventure Plate, in three short steps:");
            using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
            {
                ImGui.TextWrapped("1. Choose a look\n2. Add your name, and a portrait and message if you like\n3. Save");
                ImGui.Spacing();
                ImGui.TextWrapped("It takes a few minutes. Your Plates stay on your PC, and sharing stays off unless you turn it on.");
            }
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceMd * scale));

        if (startError is { } error)
        {
            AetherControls.StatusLine(AetherTone.Danger, error);
            ImGui.Spacing();
        }

        var busy = creating && guided.IsStarting;
        using (ImRaii.Disabled(busy))
        {
            if (AetherControls.PrimaryButton(busy ? "Creating your Plate...##WelcomeCreate" : "Create My First Plate##WelcomeCreate", new Vector2(width, 0f), "Makes a new Plate and opens it on the first step."))
            {
                creating = true;
                startError = null;
                guided.AnswerWelcome(WelcomeAnswer.Create);
            }
        }

        ImGui.Spacing();
        var half = (width - ImGui.GetStyle().ItemSpacing.X) / 2f;
        using (ImRaii.Disabled(busy))
        {
            if (AetherControls.SecondaryButton("Not Now", new Vector2(half, 0f), "Asks again another time, while you have no Plate.\nMy Plates can start your first Plate whenever you like."))
            {
                guided.AnswerWelcome(WelcomeAnswer.NotNow);
                IsOpen = false;
            }

            ImGui.SameLine();
            if (AetherControls.GhostButton("Don't Show Again", new Vector2(half, 0f), "No welcome from now on.\nMy Plates and its Help can still create a Plate step by step."))
            {
                guided.AnswerWelcome(WelcomeAnswer.Never);
                IsOpen = false;
            }
        }

        ImGui.Dummy(new Vector2(0f, AetherMetrics.SpaceSm * scale));
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width))
        {
            AetherControls.Muted("Type /af any time to open My Plates. Prefer a tour of every control? The full tutorial is under Help there.");
        }
    }
}
