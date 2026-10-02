using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Services.Art;

namespace AetherFrame.Services.Network.Transport;

/// <summary>
/// Downloads hosted artwork ("Art on demand" in the decision register) from <see cref="ArtHosting.Host"/>,
/// through the sharing connection's handler: Dalamud's connect callback, no redirect followed, no
/// cookies (decisions R2 and R3). One GET per file, at the address its compiled pin gives. The answer
/// must be a 200 of exactly the file's length; no more than that is read, and its SHA-256 must be the
/// file's before a byte of it is handed back. Compiled only in the networking flavour.
/// </summary>
internal sealed class ArtDownloadClient : IDisposable
{
    private readonly HttpClient client;

    /// <summary>A client over <paramref name="handler"/>, naming <paramref name="pluginVersion"/> in every
    /// request (R2). The plugin's handler is <c>SharingHandler</c>'s; tests pass their own.</summary>
    public ArtDownloadClient(HttpMessageHandler handler, bool disposeHandler, Version pluginVersion)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        client = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = new Uri("https://" + ArtHosting.Host + "/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AetherFrame", pluginVersion.ToString(3)));
    }

    /// <summary>How long one file may take, from sending the request to its last byte.</summary>
    public TimeSpan FileTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Downloads <paramref name="file"/>, reporting the bytes received so far to <paramref name="progress"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The file isn't well formed; nothing was sent.</exception>
    /// <exception cref="ArtDownloadException">The download failed, or its bytes weren't the file's.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was signaled.</exception>
    public async Task<byte[]> DownloadAsync(ArtFile file, IProgress<long>? progress, CancellationToken cancellation)
    {
        var path = ArtHosting.PathOf(file);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(FileTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ArtDownloadException(ArtDownloadProblem.Busy, 429);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ArtDownloadException(ArtDownloadProblem.Refused, (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is { } length && length != file.Length)
            {
                throw new ArtDownloadException(ArtDownloadProblem.WrongSize);
            }

            var bytes = new byte[file.Length];
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var received = 0;
            while (received < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(received), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ArtDownloadException(ArtDownloadProblem.WrongSize);
                }

                received += read;
                progress?.Report(received);
            }

            // One byte more than the file means it isn't the file.
            if (await stream.ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false) != 0)
            {
                throw new ArtDownloadException(ArtDownloadProblem.WrongSize);
            }

            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
            {
                throw new ArtDownloadException(ArtDownloadProblem.WrongDigest);
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("The download was cancelled.", cancellation);
        }
        catch (OperationCanceledException)
        {
            throw new ArtDownloadException(ArtDownloadProblem.TimedOut);
        }
        catch (HttpRequestException)
        {
            throw new ArtDownloadException(ArtDownloadProblem.Offline);
        }
        catch (IOException)
        {
            throw new ArtDownloadException(ArtDownloadProblem.Offline);
        }
    }

    public void Dispose() => client.Dispose();
}
