using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Profiles;
using AetherFrame.Protocol.Remote;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.UI.Theme;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;

namespace AetherFrame.Windows.Network;

/// <summary>Whether every prepared copy of a candidate has been drawn.</summary>
internal enum CandidateImages
{
    /// <summary>A copy is still being decoded for display.</summary>
    Loading,

    /// <summary>Every copy is drawn (or there is none).</summary>
    Shown,

    /// <summary>A copy couldn't be decoded for display.</summary>
    Failed,
}

/// <summary>
/// What a candidate would share, drawn for the player (N2-6's design, section 1; C3's showing): its
/// name, every text in full with the ones filled in from the character marked, each prepared image
/// as it would be sent, and what was left out and why; or why a Plate can't be shared. Every text
/// from the Plate is drawn unformatted, never inside an ImGui label, a tooltip or a format string
/// (N7). It owns the images' textures until it is released or disposed. Compiled only in the
/// networking preview flavour.
/// </summary>
internal sealed class CandidateView : IDisposable
{
    private readonly ITextureProvider textures;
    private Thumbnails? thumbnails;

    internal CandidateView(ITextureProvider textures)
    {
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
    }

    /// <summary>Draws <paramref name="candidate"/>; what it says of its images tells a caller whether a signing may follow.</summary>
    internal CandidateImages Draw(SnapshotCandidate candidate)
    {
        if (!ReferenceEquals(thumbnails?.Candidate, candidate))
        {
            Release();
            thumbnails = new Thumbnails(textures, candidate);
        }

        // Taken before anything is drawn: an image that finishes loading during this frame counts
        // from the next one, once it is on screen.
        var shown = thumbnails.Shown;

        AetherControls.SectionHeader("Its name, shared");
        Wrapped(candidate.Name);

        AetherControls.SectionHeader("Its texts, shared in full");
        var any = false;
        for (var index = 0; index < candidate.Items.Count; index++)
        {
            if (candidate.Items[index] is not LayoutText text)
            {
                continue;
            }

            any = true;
            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(text.Text.Length == 0 ? "(an empty text)" : text.Text);
            if (ShareMessages.IsFromTheCharacter(candidate.Roles[index]))
            {
                Wrapped("Filled in from your character.", AetherPalette.TextMuted);
            }
        }

        if (!any)
        {
            AetherControls.Muted("No texts.");
        }

        AetherControls.SectionHeader("Its images, as they would be shared");
        if (candidate.Images.Count == 0)
        {
            AetherControls.Muted("No images.");
        }

        var side = 128f * ImGuiHelpers.GlobalScale;
        for (var index = 0; index < candidate.Images.Count; index++)
        {
            var image = candidate.Images[index];
            var scale = Math.Min(side / image.Width, side / image.Height);
            var size = new Vector2(image.Width * scale, image.Height * scale);
            if (thumbnails?[index] is { } wrap)
            {
                ImGui.Image(wrap.Handle, size);
            }
            else
            {
                ImGui.Dummy(size);
            }

            ImGui.SameLine();
            AetherControls.Muted(string.Create(CultureInfo.InvariantCulture, $"{image.Width} x {image.Height}, {(image.Format == ImageFormat.Jpeg ? "JPEG" : "PNG")}, {ByteSize(image.ByteLength)}"));
        }

        if (candidate.LeftOut.Count > 0)
        {
            AetherControls.SectionHeader("Left out, since it isn't drawn");
            foreach (var left in candidate.LeftOut)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                Wrapped(Describe(left.Element, left.Component, false) + ": " + ShareMessages.For(left.Reason));
            }
        }

        if (shown == CandidateImages.Failed)
        {
            AetherControls.StatusLine(AetherTone.Warning, "An image couldn't be shown here, so this can't be shared. Save the Plate again to try again.");
        }

        return shown;
    }

    /// <summary>Why a Plate can't be shared as it is: each problem once, naming what it is about.</summary>
    internal static void DrawProblems(IReadOnlyList<PlateSnapshotProblem> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var problem in problems)
        {
            var line = Describe(problem.Element, problem.Component, problem.Background) + ": " + ShareMessages.For(problem.Refusal);
            if (!seen.Add(line))
            {
                continue;
            }

            ImGui.Bullet();
            ImGui.SameLine();
            Wrapped(line);
        }
    }

    /// <summary>Lets go of the images' textures; the next draw loads them again.</summary>
    internal void Release()
    {
        thumbnails?.Dispose();
        thumbnails = null;
    }

    public void Dispose() => Release();

    /// <summary>Draws text wrapped to the window, unformatted: safe for anything the player wrote.</summary>
    private static void Wrapped(string text, Vector4? color = null)
    {
        if (color is { } tint)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
        }

        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        if (color is not null)
        {
            ImGui.PopStyleColor();
        }
    }

    /// <summary>What a problem or a left-out item is about: the element's name as the editors show it (its own, a Basic role's label, or its kind), or the part of the Plate.</summary>
    private static string Describe(ProfileElement? element, PlateComponent? component, bool background)
    {
        if (element is not null)
        {
            return ProfileElementNames.GetDisplayName(element);
        }

        if (component is not null)
        {
            return "A component";
        }

        return background ? "The background" : "The Plate";
    }

    private static string ByteSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB";

    /// <summary>The prepared copies of a candidate, decoded for display; each is drawn once it has loaded, and none after <see cref="Dispose"/>.</summary>
    private sealed class Thumbnails : IDisposable
    {
        private readonly IDalamudTextureWrap?[] wraps;
        private int loaded;
        private bool failed;
        private bool disposed;

        internal Thumbnails(ITextureProvider textures, SnapshotCandidate candidate)
        {
            Candidate = candidate;
            wraps = new IDalamudTextureWrap?[candidate.ImageBytes.Count];
            for (var index = 0; index < wraps.Length; index++)
            {
                _ = LoadAsync(textures, candidate.ImageBytes[index], index);
            }
        }

        internal SnapshotCandidate Candidate { get; }

        /// <summary>Whether every copy is drawn, one is still loading, or one failed.</summary>
        internal CandidateImages Shown
        {
            get
            {
                lock (wraps)
                {
                    return failed ? CandidateImages.Failed : loaded == wraps.Length ? CandidateImages.Shown : CandidateImages.Loading;
                }
            }
        }

        internal IDalamudTextureWrap? this[int index]
        {
            get
            {
                lock (wraps)
                {
                    return index < wraps.Length ? wraps[index] : null;
                }
            }
        }

        public void Dispose()
        {
            lock (wraps)
            {
                disposed = true;
                for (var index = 0; index < wraps.Length; index++)
                {
                    wraps[index]?.Dispose();
                    wraps[index] = null;
                }
            }
        }

        private async Task LoadAsync(ITextureProvider textures, ReadOnlyMemory<byte> bytes, int index)
        {
            try
            {
                var wrap = await textures.CreateFromImageAsync(bytes, "AetherFrame sharing").ConfigureAwait(false);
                lock (wraps)
                {
                    if (disposed)
                    {
                        wrap.Dispose();
                        return;
                    }

                    wraps[index] = wrap;
                    loaded++;
                }
            }
            catch (Exception)
            {
                // The copy was checked when it was prepared; one that can't be shown here can't be shared.
                lock (wraps)
                {
                    failed = true;
                }
            }
        }
    }
}
