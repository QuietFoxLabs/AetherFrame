using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace AetherFrame.Hosting.Network.Publishing;

/// <summary>
/// Image preparation's codec over Dalamud's texture pipeline (decision D5 and its N2-6 note):
/// <list type="bullet">
/// <item>decoding: WIC decodes the managed image as the renderer's textures are decoded, and the
/// texture is read back whole, never through <c>TextureModificationArgs</c>, which can
/// resample. Its colour comes back premultiplied by its alpha, though its DXGI format is the
/// straight B8G8R8A8: measured in game on October 1, 2026, when the known image's pixel 90, 180,
/// 45 at alpha 64 came back as 23, 45, 11. So every decode is declared premultiplied, and the
/// session's known-answer check fails if that ever stops being true;</item>
/// <item>encoding: the prepared pixels become a raw RGBA texture, which WIC encodes as N2-6's
/// design sets it: a JPEG at quality 0.92 (a float, VT_R4) with 4:2:0 chroma and its JFIF APP0
/// kept, or a PNG, not interlaced.</item>
/// </list>
/// Every failure is a null, never an exception, except a cancellation, and is remembered by its
/// exception's kind and HRESULT for the check's diagnosis. Compiled only in the networking preview
/// flavour.
/// </summary>
internal sealed class DalamudImageCodec : IImageCodec, IImageCodecFailures
{
    // WIC's container formats (wincodec.h): GUID_ContainerFormatPng and GUID_ContainerFormatJpeg.
    private static readonly Guid PngContainer = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    // WIC's encoder options (Microsoft's "Image Encoding Overview"): JpegYCrCbSubsampling is a
    // WICJpegYCrCbSubsamplingOption (VT_UI1), whose 4:2:0 is 1.
    private static readonly IReadOnlyDictionary<string, object> JpegOptions = new Dictionary<string, object>
    {
        ["ImageQuality"] = 0.92f,
        ["JpegYCrCbSubsampling"] = (byte)1,
        ["SuppressApp0"] = false,
    };

    private static readonly IReadOnlyDictionary<string, object> PngOptions = new Dictionary<string, object>
    {
        ["InterlaceOption"] = false,
    };

    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private volatile string? lastFailure;

    internal DalamudImageCodec(ITextureProvider textures, ITextureReadbackProvider readback)
    {
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.readback = readback ?? throw new ArgumentNullException(nameof(readback));
    }

    /// <inheritdoc/>
    public string? LastFailure => lastFailure;

    public async Task<DecodedImage?> DecodeAsync(ReadOnlyMemory<byte> file, CancellationToken cancellation)
    {
        IDalamudTextureWrap wrap;
        try
        {
            wrap = await textures.CreateFromImageAsync(file, "AetherFrame publishing", cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The file doesn't decode, as the renderer finds it.
            Remember("decode", e);
            return null;
        }

        using (wrap)
        {
            try
            {
                var (specification, pixels) = await readback.GetRawImageAsync(wrap, default, leaveWrapOpen: true, cancellation).ConfigureAwait(false);
                return new DecodedImage(specification.Width, specification.Height, specification.Pitch, specification.DxgiFormat, pixels, Premultiplied: true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Remember("read-back", e);
                return DecodedImage.ReadBackFailed;
            }
        }
    }

    public async Task<byte[]?> EncodeAsync(ReadOnlyMemory<byte> rgba, int width, int height, ImageFormat format, CancellationToken cancellation)
    {
        try
        {
            using var wrap = textures.CreateFromRaw(RawImageSpecification.Rgba32(width, height), rgba.Span, "AetherFrame publishing");
            using var stream = new MemoryStream();
            var (container, options) = format == ImageFormat.Jpeg ? (JpegContainer, JpegOptions) : (PngContainer, PngOptions);
            await readback.SaveToStreamAsync(wrap, container, stream, options, leaveWrapOpen: true, leaveStreamOpen: true, cancellation).ConfigureAwait(false);
            return stream.ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Remember("encode", e);
            return null;
        }
    }

    private void Remember(string step, Exception e) =>
        lastFailure = step + ": " + e.GetType().Name + " 0x" + e.HResult.ToString("X8", CultureInfo.InvariantCulture);
}
