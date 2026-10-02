using AetherFrame.Domain.Rendering;
using AetherFrame.Services.Art;
using AetherFrame.UI.Rendering;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetherFrame.Windows;

/// <summary>
/// The editors' line about artwork a Plate is missing (art on demand): what is downloading and how
/// far it has got, or why it failed with a Try again button, or that this build can't download it.
/// Drawn on the line of a toolbar, after its last item; nothing at all when nothing is missing.
/// </summary>
internal static class ArtDownloadStatus
{
    internal static void DrawInline(ArtNeeds needs, ArtStore store)
    {
        var summary = needs.Summary(store);
        if (summary.Kind == ArtNeedKind.None)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, summary.Kind == ArtNeedKind.Failed ? AetherPalette.Warning : EditorWidgets.DimTextColor))
        {
            ImGui.TextUnformatted(summary.Label);
        }

        if (summary.Kind == ArtNeedKind.Downloading)
        {
            EditorWidgets.Tooltip("Art Styles' artwork downloads from GitHub the first time it is used, and is kept on this PC.");
        }

        if (summary.Kind == ArtNeedKind.Failed)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Try again##ArtTryAgain"))
            {
                needs.TryAgain(store);
            }

            EditorWidgets.Tooltip("Download the missing artwork from GitHub again");
        }
    }
}
