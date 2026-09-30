using System;
using System.IO;
using AetherFrame.Services;
using AetherFrame.Services.Assets;
using AetherFrame.Services.Network.Publishing;

namespace AetherFrame.Hosting.Network.Publishing;

/// <summary>
/// The managed images' files, as image preparation reads them: the file managed storage holds for
/// an asset id, whole, up to the size import allows. Anything missing, unreadable or larger reads
/// as missing. Compiled only in the networking preview flavour.
/// </summary>
internal sealed class ManagedImageFiles : IManagedImages
{
    private readonly AssetStorageService assets;

    internal ManagedImageFiles(AssetStorageService assets)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    public byte[]? TryRead(Guid image)
    {
        try
        {
            if (assets.ResolveAssetPath(image) is not { } path)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            if (stream.Length > ImageSafety.MaxFileBytes)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
