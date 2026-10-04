using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using AetherFrame.Windows.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherFrame.Windows;

/// <summary>
/// The fonts' licences, as the plugin itself carries them (embedded beside the fonts), reached from
/// Help: AetherFrame's own three families under the SIL Open Font License, and the font library's
/// families each under the SIL Open Font License or the Apache License, with every family's
/// copyright notice. The SIL Open Font License and the Apache License (section 4(a)) ask that
/// recipients of the fonts get the licences with them; this is where a player can read them.
/// The text is read once, when the window first opens.
/// </summary>
internal sealed class FontLicencesWindow : Window
{
    private readonly AetherWindowChrome chrome = new();
    private string? text;

    internal FontLicencesWindow()
        : base("Font licences##AetherFrameFontLicences", ImGuiWindowFlags.NoCollapse)
    {
        Size = new Vector2(640f, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    /// <summary>The licence texts, read from the plugin's own resources.</summary>
    internal static string ReadNotices()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var notices = new System.Text.StringBuilder();
        foreach (var resource in AetherFrame.Domain.Rendering.FontLibrary.NoticeResources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded licence '{resource}' was not found.");
            using var reader = new StreamReader(stream);
            notices.Append(reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
            notices.Append("\n\n");
        }

        return notices.ToString();
    }

    public override void PreDraw()
    {
        chrome.PushStyle();
        AetherWindowChrome.ApplyPolicy(this);
    }

    public override void PostDraw() => chrome.PopStyle();

    public override void Draw()
    {
        try
        {
            text ??= ReadNotices();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            text = "The licences couldn't be read from this AetherFrame (" + exception.GetType().Name + "). They are in AetherFrame's repository, in AetherFrame/Fonts.";
        }

        ImGui.TextUnformatted("AetherFrame's fonts are free software, each under its own licence below.");
        ImGui.Separator();

        // Unwrapped, so ImGui draws only the lines in view of this long text.
        using var child = AetherChild.Begin("##FontLicenceText", new Vector2(-1f, -1f), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (child.Success)
        {
            ImGui.TextUnformatted(text);
        }
    }
}
