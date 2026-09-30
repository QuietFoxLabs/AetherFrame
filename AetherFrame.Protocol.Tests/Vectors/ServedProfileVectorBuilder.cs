using System;
using System.Collections.Generic;
using System.Linq;
using AetherFrame.Protocol.Remote;
using static AetherFrame.Protocol.Tests.ReferenceServed;

namespace AetherFrame.Protocol.Tests;

/// <summary>
/// The served profile vectors (docs/networking/ProtocolSpecification-v1.md, sections 8.6 and 11):
/// two valid bodies, and bodies that must be refused, each written by <see cref="ReferenceServed"/>
/// from the rich sample's served form with one change. Nothing here is signed, so every value is
/// stable across regenerations. The size limit is covered by the unit tests, since its body would
/// be a megabyte.
/// </summary>
internal static class ServedProfileVectorBuilder
{
    public static List<ServedProfileVector> BuildValid(Func<string, byte[]> document)
    {
        var layout = (ProfileLayoutSnapshot)Documents.SignedDocumentCodec.Verify(document("profile-layout-snapshot")).Document;
        return
        [
            new ServedProfileVector
            {
                Name = "served-profile-layout-snapshot",
                Document = "profile-layout-snapshot",
                Marker = Marker.ToString(),
                Body = Hex.Of(ServedProfile.Build(layout, Marker)),
            },
            new ServedProfileVector
            {
                Name = "served-minimal",
                Construction = "the served profile of a schema 2 snapshot named A, with a canvas of 128000 x 72000, no items, no images, and a background that draws nothing: mode 0, both colours 00000000, angle 0, opacity 255, no pattern, intensity 0, scale 2400, rotation 0, no image (255), fit 0, no flips",
                Marker = Marker.ToString(),
                Body = Hex.Of(ServedProfile.Build(LayoutSamples.Minimal(), Marker)),
            },
        ];
    }

    public static List<RejectedServedVector> BuildRejected()
    {
        var list = new List<RejectedServedVector>();
        void Add(string name, byte[] body, ProtocolError error, string reason) =>
            list.Add(new RejectedServedVector { Name = name, Body = Hex.Of(body), Error = error.ToString(), Reason = reason });
        void With(string name, Action<ServedSpec> change, ProtocolError error, string reason)
        {
            var spec = new ServedSpec();
            change(spec);
            Add(name, Write(spec), error, reason);
        }

        var valid = Write(new ServedSpec());

        // Framing and version.
        Add("served-empty", [], ProtocolError.Truncated, "no bytes");
        Add("served-truncated-last-byte", valid[..^1], ProtocolError.Truncated, "one byte short");
        With("served-document-magic", s => s.Magic = "AFPD"u8.ToArray(), ProtocolError.InvalidFraming, "a signed document's magic: a document is never read as a served profile");
        With("served-request-proof-magic", s => s.Magic = "AFRQ"u8.ToArray(), ProtocolError.InvalidFraming, "a request proof's magic");
        With("served-version-2", s => s.Version = 2, ProtocolError.UnsupportedVersion, "version 2");
        With("served-version-draft-marker", s => s.Version = 0x8001, ProtocolError.UnsupportedVersion, "0x8001, the documents' draft marker: a served profile has none");
        With("served-marker-all-zero", s => s.Marker = new byte[16], ProtocolError.InvalidValue, "the all-zero marker");
        With("served-name-empty", s => s.Name = "", ProtocolError.InvalidLength, "an empty name: the name rule of section 8.1.1 applies");
        With("served-canvas-width-99", s => s.CanvasWidth = 99, ProtocolError.InvalidValue, "a canvas one below the smallest");

        // Images named by index.
        With("served-background-index-8", s => s.BackgroundImage = 8, ProtocolError.InvalidValue, "index 8: a served profile has at most eight images, 0 to 7");
        With("served-background-index-254", s => s.BackgroundImage = 254, ProtocolError.InvalidValue, "index 254, neither an index nor none (255)");
        With("served-background-image-mode-without-image", s => s.BackgroundImage = 255, ProtocolError.InvalidValue, "the image mode naming no image");
        With("served-background-image-without-image-mode", s => s.BackgroundMode = 1, ProtocolError.InvalidValue, "a solid colour naming image 0");
        With("served-item-image-none", s => s.Items = RichItems(imageIndex: 255), ProtocolError.InvalidValue, "an image item naming no image: only a background may");
        With("served-item-image-index-8", s => s.Items = RichItems(imageIndex: 8), ProtocolError.InvalidValue, "an image item naming index 8");
        With("served-item-image-not-listed", s => s.Items = RichItems(imageIndex: 2), ProtocolError.InvalidValue, "an image item naming index 2 of a list of two, refused by the rule over the whole body");
        With("served-image-never-drawn", s => s.Items = RichItems(imageIndex: 0), ProtocolError.InvalidValue, "the JPEG listed but drawn by nothing");

        // The image list.
        With("served-image-webp", s => s.Images[1] = (3, 1920, 1080), ProtocolError.InvalidValue, "format 3, WebP, which a served profile never carries");
        With("served-image-format-0", s => s.Images[1] = (0, 1920, 1080), ProtocolError.InvalidValue, "format 0");
        With("served-image-width-0", s => s.Images[1] = (2, 0, 1080), ProtocolError.InvalidValue, "a width of 0");
        With("served-image-width-8193", s => s.Images[1] = (2, 8193, 1080), ProtocolError.LimitExceeded, "a width one over the limit");
        With("served-image-over-pixel-limit", s => s.Images[1] = (2, 8192, 2442), ProtocolError.LimitExceeded, "8192 x 2442, just over 20,000,000 pixels");
        With("served-images-over-total-pixels", s =>
        {
            s.Items = RichItems(imageIndex: 1, quadIndex: 2);
            s.Images = [(1, 8192, 2048), (2, 8192, 2048), (1, 8192, 2048)];
        }, ProtocolError.LimitExceeded, "three images of 8192 x 2048, each drawn: 50,331,648 pixels in all, over 33,554,432");
        With("served-nine-images", s => s.ImageCount = 9, ProtocolError.LimitExceeded, "a count of nine images, refused before any is read");
        With("served-items-over-limit", s => s.ItemCount = 2049, ProtocolError.LimitExceeded, "a count of 2,049 items, refused before any is read");
        With("served-texts-over-total", s => s.Items = [.. RichItems(), .. Enumerable.Repeat<Action<ReferenceLayout.Bytes>>(w => Text(w, new string('a', 2048)), 16)], ProtocolError.LimitExceeded, "sixteen texts of 2,048 characters besides the sample's: over 32,000 in all");
        With("served-trailing-byte", s => s.Trailing = [0], ProtocolError.TrailingBytes, "one byte after the image list");
        return list;
    }
}
