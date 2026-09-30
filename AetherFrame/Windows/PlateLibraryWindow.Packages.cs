using System;
using AetherFrame.Services.Packages;

namespace AetherFrame.Windows;

/// <summary>
/// My Plates: Import of .aetherframe files. It starts only from the player's own choice of file,
/// and hands that file to the Import Preview, which validates it before showing anything. Export
/// is an action on a Plate, so it belongs to the shared <see cref="PlateMenu"/>, which never
/// replaces an existing file without asking.
/// </summary>
internal sealed partial class PlateLibraryWindow
{
    private const string PackageFilter = "AetherFrame Plate{" + PackagePolicy.FileExtension + "}";

    /// <summary>Called by the Import Preview after a successful import.</summary>
    internal void OnPlateImported(Guid plateId, string name)
    {
        selectedPlateId = plateId;
        searchText = string.Empty;
        runner.Error = null;
        runner.Status = $"Imported \"{name}\" as a new Plate.";
        IsOpen = true;
    }

    private void OpenImportDialog() =>
        fileDialogManager.OpenFileDialog("Import Plate", PackageFilter, (chosen, path) =>
        {
            if (chosen && !string.IsNullOrWhiteSpace(path))
            {
                beginImport(path);
            }
        });
}
