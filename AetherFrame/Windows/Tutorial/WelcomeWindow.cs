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
/// One line and the three steps' names; everything else (where Plates are kept, that sharing stays
/// off, the full tutorial under Help) is in the buttons' tooltips.
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
    /// failure, which it shows) leaves the welcome open to answer again. Closes unanswered once a
    /// Plate is made another way (My Plates, Help, Create Plate), and steps aside, to come back while
    /// My Plates is still empty, when the character logs out or a recovery offer comes up, whether or
    /// not it shows an error (<see cref="GuidedCreation.WelcomeOnScreenNow"/>). The error it shows is
    /// dropped once guided creation no longer reports it (<see cref="GuidedCreation.WelcomeErrorNow"/>).
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

        if (!IsOpen)
        {
            return;
        }

        if (creating && !guided.IsStarting)
        {
            creating = false;
            startError = guided.StartError;
            if (startError is null && guided.CanContinue)
            {
                // The Plate is made and open in the Basic editor, on its first step.
                IsOpen = false;
                return;
            }
        }
        else if (!creating)
        {
            startError = GuidedCreation.WelcomeErrorNow(startError, guided.StartError);
        }

        switch (GuidedCreation.WelcomeOnScreenNow(creating, startError is not null, guided.HasPlate, guided.CanContinue && !guided.StepsOnScreen, mayShow()))
        {
            case WelcomeOnScreen.Closes:
                IsOpen = false;
                break;
            case WelcomeOnScreen.StepsAside:
                IsOpen = false;
                guided.WithdrawWelcome();
                break;
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
            ImGui.TextWrapped("Create a character card in three short steps.");
            using (ImRaii.PushColor(ImGuiCol.Text, AetherPalette.TextSecondary))
            {
                ImGui.TextWrapped("1. Choose a look     2. Make it yours     3. Save");
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
            if (AetherControls.PrimaryButton(busy ? "Creating your Plate...##WelcomeCreate" : "Create My First Plate##WelcomeCreate", new Vector2(width, 0f), "A Plate is your character's card. This makes one and opens it on the first step.\nYour Plates stay on your PC, and sharing stays off unless you turn it on."))
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
            if (AetherControls.SecondaryButton("Not Now", new Vector2(half, 0f), "Close this for now. It may ask again when AetherFrame next loads, while you have no Plate.\nType /af any time to open My Plates; the full tutorial is under Help there."))
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
    }
}
