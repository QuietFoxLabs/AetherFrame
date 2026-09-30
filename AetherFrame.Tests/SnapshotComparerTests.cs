using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The commit's check that the signed bytes say what was shown (N2-6c; N2-6's design, section 2,
/// step 8): two snapshots are the same only when every field is. Each item, the background and each
/// image declaration is built again with one constructor argument changed at a time, and every one
/// of those differences is seen; a field the protocol gains changes a constructor, which fails here
/// until the comparison covers it.
/// </summary>
public sealed class SnapshotComparerTests
{
    private static readonly AssetId First = AssetId.NewId();
    private static readonly AssetId Second = AssetId.NewId();
    private static readonly LayoutPoint P1 = new(100, 200);
    private static readonly LayoutPoint P2 = new(300, 400);
    private static readonly LayoutPoint P3 = new(500, 600);
    private static readonly LayoutPoint P4 = new(700, 800);
    private static readonly LayoutColor C1 = new(1, 2, 3, 4);
    private static readonly LayoutColor C2 = new(5, 6, 7, 8);

    public static TheoryData<string> ItemKinds => new(Items.Keys);

    /// <summary>For each item kind: its constructor's arguments, then another valid value for each.</summary>
    private static Dictionary<string, (Type Type, object[] Base, object[] Other)> Items => new()
    {
        ["text"] = (typeof(LayoutText),
            [P1, 5000, 1000, "Hello", "dalamud-default", 1800, C1, LayoutHorizontalAlign.Left, LayoutVerticalAlign.Top, LayoutTextFlags.None, 0, 0, 100, C1, 0, C2, 0, 0, LayoutTextLayout.Current],
            [P2, 5001, 1001, "Hellp", "dalamud-defaults", 1801, C2, LayoutHorizontalAlign.Center, LayoutVerticalAlign.Middle, LayoutTextFlags.Wrap, 1, 1, 101, C2, 1, C1, 1, 1, LayoutTextLayout.Legacy]),
        ["image"] = (typeof(LayoutImage),
            [First, P1, 800, 400, 0, LayoutImageFit.Stretch, LayoutFlips.None, (byte)255],
            [Second, P2, 801, 401, 1, LayoutImageFit.Fit, LayoutFlips.Horizontal, (byte)254]),
        ["quad"] = (typeof(LayoutQuad), [P1, P2, P3, P4, C1], [P2, P3, P4, P1, C2]),
        ["triangle"] = (typeof(LayoutTriangle), [P1, P2, P3, C1], [P2, P3, P1, C2]),
        ["image quad"] = (typeof(LayoutImageQuad), [First, P1, P2, P3, P4, C1], [Second, P2, P3, P4, P1, C2]),
        ["art quad"] = (typeof(LayoutArtQuad), ["celestial-dream", P1, P2, P3, P4, C1], ["celestial-sakura", P2, P3, P4, P1, C2]),
    };

    [Theory]
    [MemberData(nameof(ItemKinds))]
    public void AnItem_IsTheSame_OnlyWhenEveryFieldIs(string kind)
    {
        var (type, arguments, others) = Items[kind];
        var constructor = type.GetConstructors().Single();
        Assert.Equal(constructor.GetParameters().Length, arguments.Length);
        Assert.Equal(arguments.Length, others.Length);

        var item = (LayoutItem)constructor.Invoke(arguments);
        Assert.True(SnapshotComparer.Same(item, (LayoutItem)constructor.Invoke(arguments)));
        for (var index = 0; index < arguments.Length; index++)
        {
            var changed = (object[])arguments.Clone();
            changed[index] = others[index];
            Assert.False(SnapshotComparer.Same(item, (LayoutItem)constructor.Invoke(changed)), $"{kind}: {constructor.GetParameters()[index].Name}");
        }
    }

    [Fact]
    public void ItemsOfDifferentKinds_AreNeverTheSame()
    {
        var quad = new LayoutQuad(P1, P2, P3, P4, C1);
        var imageQuad = new LayoutImageQuad(First, P1, P2, P3, P4, C1);
        Assert.False(SnapshotComparer.Same(quad, imageQuad));
        Assert.False(SnapshotComparer.Same(imageQuad, quad));
    }

    [Fact]
    public void ABackground_IsTheSame_OnlyWhenEveryFieldIs()
    {
        object[] arguments = [LayoutBackgroundMode.SolidColor, C1, C2, 0, (byte)255, LayoutTexture.None, (byte)0, LayoutBackground.MinTextureScale, 0, default(AssetId), LayoutImageFit.Stretch, LayoutFlips.None];
        object?[] others = [LayoutBackgroundMode.LinearGradient, C2, C1, 1, (byte)254, (LayoutTexture)1, (byte)1, LayoutBackground.MinTextureScale + 1, 1, null, LayoutImageFit.Fit, LayoutFlips.Vertical];
        var constructor = typeof(LayoutBackground).GetConstructors().Single();
        Assert.Equal(constructor.GetParameters().Length, arguments.Length);

        var background = (LayoutBackground)constructor.Invoke(arguments);
        Assert.True(SnapshotComparer.Same(background, (LayoutBackground)constructor.Invoke(arguments)));
        for (var index = 0; index < arguments.Length; index++)
        {
            if (others[index] is null)
            {
                continue;
            }

            var changed = (object[])arguments.Clone();
            changed[index] = others[index]!;
            Assert.False(SnapshotComparer.Same(background, (LayoutBackground)constructor.Invoke(changed)), constructor.GetParameters()[index].Name);
        }

        // The image, which only the image mode names.
        var image = new LayoutBackground(LayoutBackgroundMode.Image, C1, C2, 0, 255, LayoutTexture.None, 0, LayoutBackground.MinTextureScale, 0, First, LayoutImageFit.Fill, LayoutFlips.None);
        var another = new LayoutBackground(LayoutBackgroundMode.Image, C1, C2, 0, 255, LayoutTexture.None, 0, LayoutBackground.MinTextureScale, 0, Second, LayoutImageFit.Fill, LayoutFlips.None);
        Assert.False(SnapshotComparer.Same(image, another));
    }

    [Fact]
    public void AnImageDeclaration_IsTheSame_OnlyWhenEveryFieldIs()
    {
        var digest = SHA256.HashData([1]);
        var declared = new ImageReference(First, digest, ImageFormat.Png, 100, 8, 4);
        Assert.True(SnapshotComparer.Same(declared, new ImageReference(First, (byte[])digest.Clone(), ImageFormat.Png, 100, 8, 4)));

        foreach (var other in new[]
        {
            new ImageReference(Second, digest, ImageFormat.Png, 100, 8, 4),
            new ImageReference(First, SHA256.HashData([2]), ImageFormat.Png, 100, 8, 4),
            new ImageReference(First, digest, ImageFormat.Jpeg, 100, 8, 4),
            new ImageReference(First, digest, ImageFormat.Png, 101, 8, 4),
            new ImageReference(First, digest, ImageFormat.Png, 100, 9, 4),
            new ImageReference(First, digest, ImageFormat.Png, 100, 8, 5),
        })
        {
            Assert.False(SnapshotComparer.Same(declared, other));
        }

        Assert.Equal(6, typeof(ImageReference).GetConstructors().Single().GetParameters().Length);
    }

    [Fact]
    public void ASnapshot_IsTheSame_OnlyWhenEveryFieldIsItemForItem()
    {
        var profile = ProfileId.NewId();
        var revision = RevisionId.NewId();
        var png = PreparedPngs.Png(8, 4);
        var declared = new ImageReference(First, SHA256.HashData(png), ImageFormat.Png, png.Length, 8, 4);
        var image = new LayoutImage(First, P1, 800, 400, 0, LayoutImageFit.Stretch, LayoutFlips.None, 255);
        var text = new LayoutText(P1, 5000, 1000, "Hello", "dalamud-default", 1800, C1, LayoutHorizontalAlign.Left, LayoutVerticalAlign.Top, LayoutTextFlags.None, 0, 0, 100, C1, 0, C2, 0, 0, LayoutTextLayout.Current);

        ProfileLayoutSnapshot Snapshot(ProfileId? id = null, RevisionId? rev = null, long createdAt = 1_790_000_000, string name = "Shared", int width = 128_000, int height = 72_000, LayoutBackground? background = null, LayoutItem[]? items = null, ImageReference[]? images = null) =>
            new(id ?? profile, rev ?? revision, createdAt, name, width, height, background ?? LayoutBackground.None, items ?? [text, image], images ?? [declared]);

        // Every argument of the snapshot's constructor is varied below.
        Assert.Equal(9, typeof(ProfileLayoutSnapshot).GetConstructors().Single().GetParameters().Length);

        var snapshot = Snapshot();
        Assert.True(SnapshotComparer.Same(snapshot, Snapshot()));

        var otherPng = PreparedPngs.Png(8, 4, seed: 9);
        foreach (var other in new[]
        {
            Snapshot(id: ProfileId.NewId()),
            Snapshot(rev: RevisionId.NewId()),
            Snapshot(createdAt: 1_790_000_001),
            Snapshot(name: "Shares"),
            Snapshot(width: 128_001),
            Snapshot(height: 72_001),
            Snapshot(background: new LayoutBackground(LayoutBackgroundMode.SolidColor, C1, C2, 0, 255, LayoutTexture.None, 0, LayoutBackground.MinTextureScale, 0, default, LayoutImageFit.Stretch, LayoutFlips.None)),
            Snapshot(items: [image, text]),
            Snapshot(items: [text, image, text]),
            Snapshot(images: [new ImageReference(First, SHA256.HashData(otherPng), ImageFormat.Png, otherPng.Length, 8, 4)]),
        })
        {
            Assert.False(SnapshotComparer.Same(snapshot, other));
        }
    }
}
