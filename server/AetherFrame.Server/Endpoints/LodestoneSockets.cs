using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace AetherFrame.Server.Endpoints;

/// <summary>
/// The Lodestone check and re-read sent as a WebSocket at their own paths (ServerApi-v1.md, section
/// 2.3; "Checking a character through the player's own connection" in the decision register), so the
/// page is read through the player's own connection. Before the upgrade: C6's address limit, taken
/// here once and never again for the signed body; no <c>Origin</c> header, since plugins send none;
/// and a place among at most <see cref="PerAddressGroup"/> open WebSockets for each of the address's
/// groups (<see cref="AddressGroups"/>, with its multiples) and <see cref="Total"/> in all. Kestrel's
/// own bound on upgraded connections, <see cref="UpgradedConnectionBound"/>, is an outer one.
/// </summary>
internal sealed class LodestoneSockets(SignedRequests requests, RateLimiter limiter, LodestoneActions actions, PipedReads reads, IPipedPages pages)
{
    /// <summary>The most WebSockets open at once for one address group, times the group's multiple.</summary>
    public const int PerAddressGroup = 2;

    /// <summary>The most WebSockets open at once in all.</summary>
    public const int Total = 20;

    /// <summary>
    /// Kestrel's own limit on upgraded connections: an outer bound, above <see cref="Total"/>, since
    /// Kestrel counts a connection out only once it has closed, a moment after its place is released.
    /// </summary>
    public const int UpgradedConnectionBound = 32;

    /// <summary>The largest message after the first, either way.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>The most bytes the server sends toward the Lodestone through one pipe.</summary>
    public const int MaxBytesToLodestone = 16 * 1024;

    /// <summary>The most bytes the plugin may send from the Lodestone through one pipe.</summary>
    public const int MaxBytesFromLodestone = 2 * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, int> openByGroup = new(StringComparer.Ordinal);
    private int open;

    /// <summary>How long after the upgrade the first message may come.</summary>
    internal TimeSpan FirstMessageDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after <c>open</c> the plugin's <c>opened</c> may come.</summary>
    internal TimeSpan OpenedDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after <c>open</c> the fetch must end, the wait for <c>opened</c> included.</summary>
    internal TimeSpan FetchDeadline { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after the upgrade the whole session must end: room for the first message, the
    /// fetch, the delay after the read and the final answer (Caddy's own bound is 60 seconds).
    /// </summary>
    internal TimeSpan SessionDeadline { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>How many WebSockets are open now: for tests.</summary>
    internal int Open
    {
        get
        {
            lock (gate)
            {
                return open;
            }
        }
    }

    /// <summary>
    /// Runs one WebSocket for <paramref name="kind"/>, the check or the re-read, from the checks before
    /// the upgrade to its final message. A refusal before the upgrade is a status with no body.
    /// </summary>
    public async Task<IResult> RunAsync(HttpContext http, RequestProofKind kind)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            return SignedRequests.Fail(http, StatusCodes.Status400BadRequest, "socket:not-websocket");
        }

        var address = http.Connection.RemoteIpAddress;
        if (!limiter.TryTakeAddress(ServerLimits.LodestonePerAddress, address))
        {
            return SignedRequests.Fail(http, StatusCodes.Status429TooManyRequests, "limit:" + ServerLimits.LodestonePerAddress.Name);
        }

        if (http.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            return SignedRequests.Fail(http, StatusCodes.Status403Forbidden, "socket:origin");
        }

        using var place = TryOpen(address);
        if (place is null)
        {
            return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "socket:busy");
        }

        WebSocket socket;
        try
        {
            socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        }
        catch (InvalidOperationException) when (!http.Response.HasStarted)
        {
            // Kestrel's own bound on upgraded connections.
            return SignedRequests.Fail(http, StatusCodes.Status503ServiceUnavailable, "socket:busy");
        }

        using (socket)
        using (var session = new PipeSession(socket, this, reads, pages))
        {
            http.Items[SignedRequests.ErrorKindItem] = await session.RunAsync((body, cancellation) => ActAsync(kind, body, address, session, cancellation), http.RequestAborted);
        }

        return Results.Empty;
    }

    /// <summary>A place among the open WebSockets for <paramref name="address"/>, or null when its groups or the server are full.</summary>
    internal IDisposable? TryOpen(IPAddress? address)
    {
        var groups = AddressGroups.Of(address).ToArray();
        lock (gate)
        {
            if (open >= Total || groups.Any(entry => openByGroup.GetValueOrDefault(entry.Group) >= PerAddressGroup * entry.Multiple))
            {
                return null;
            }

            open++;
            foreach (var (group, _) in groups)
            {
                openByGroup[group] = openByGroup.GetValueOrDefault(group) + 1;
            }
        }

        return new Place(this, groups);
    }

    /// <summary>The signed body checked as a <c>POST</c>'s is, then the action, with the page read through <paramref name="session"/>.</summary>
    private async Task<ActionAnswer> ActAsync(RequestProofKind kind, byte[] body, IPAddress? address, PipeSession session, CancellationToken cancellation)
    {
        var (action, refusal) = await requests.VerifyAsync(body, kind, address, cancellation);
        if (action is null)
        {
            return refusal!;
        }

        return kind == RequestProofKind.LodestoneCheck
            ? await actions.CheckAsync(action, address, session, cancellation)
            : await actions.RereadAsync(action, session, cancellation);
    }

    private void Release((string Group, int Multiple)[] groups)
    {
        lock (gate)
        {
            open--;
            foreach (var (group, _) in groups)
            {
                if (--openByGroup[group] == 0)
                {
                    openByGroup.Remove(group);
                }
            }
        }
    }

    private sealed class Place(LodestoneSockets sockets, (string Group, int Multiple)[] groups) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                sockets.Release(groups);
            }
        }
    }
}

/// <summary>
/// One WebSocket's exchange (ServerApi-v1.md, section 2.3). Its first message is the signed body a
/// <c>POST</c> carries. When the action needs the page, the server sends <c>open</c>, once; the plugin
/// answers <c>opened</c> or <c>failed</c>; binary messages then carry the plugin's one connection to
/// the Lodestone both ways, and the plugin sends <c>eof</c> when the Lodestone closes its side. The
/// server ends the pipe with <c>close</c>, then sends the final message and closes the WebSocket.
/// Anything the exchange doesn't expect, a limit passed or a deadline missed ends the session at once:
/// the WebSocket is dropped, with no final message.
/// </summary>
internal sealed class PipeSession(WebSocket socket, LodestoneSockets limits, PipedReads reads, IPipedPages pages) : IDisposable
{
    private static readonly byte[] OpenMessage = "open"u8.ToArray();
    private static readonly byte[] CloseMessage = "close"u8.ToArray();

    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly Channel<byte[]> fromLodestone = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<bool> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stage stage = Stage.Waiting;
    private bool openSent;
    private Opening opening = Opening.Unanswered;
    private bool lodestoneEnded;
    private long bytesFromLodestone;
    private long bytesToLodestone;
    private string? cut;
    private CancellationToken session;
    private Task receiving = Task.CompletedTask;

    /// <summary>Where the exchange is.</summary>
    private enum Stage
    {
        /// <summary>Before <c>open</c>: the plugin may send nothing after its first message.</summary>
        Waiting,

        /// <summary><c>open</c> is sent: <c>opened</c> or <c>failed</c> may come.</summary>
        Opening,

        /// <summary>The pipe carries bytes both ways.</summary>
        Open,

        /// <summary><c>close</c> is sent: bytes, <c>eof</c> or a late answer to <c>open</c> already in flight are dropped.</summary>
        Closed,

        /// <summary>The final message is sent: what was in flight is dropped, and the plugin's close ends the session.</summary>
        Finished,
    }

    /// <summary>The plugin's answer to <c>open</c>.</summary>
    private enum Opening
    {
        Unanswered,
        Opened,
        Failed,
    }

    /// <summary>
    /// Runs the session: the first message within its deadline, then <paramref name="act"/> on it,
    /// then the final message and the close. The kind the request log records: the final message's
    /// status and kind, or why the session was cut.
    /// </summary>
    public async Task<string> RunAsync(Func<byte[], CancellationToken, Task<ActionAnswer>> act, CancellationToken aborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted, stop.Token);
        lifetime.CancelAfter(limits.SessionDeadline);
        session = lifetime.Token;
        string? outcome = null;
        try
        {
            var body = await ReceiveFirstAsync();
            if (body is null)
            {
                return "socket:cut:" + cut;
            }

            receiving = ReceiveAsync();
            var answer = await act(body, session);
            lock (gate)
            {
                stage = Stage.Finished;
            }

            await SendAsync(answer.ToFinalMessage(), WebSocketMessageType.Text);
            outcome = "socket:" + answer.Status + (answer.Kind is null ? "" : ":" + answer.Kind);

            // The plugin closes in answer, within the session's time.
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, session);
            await receiving.WaitAsync(session);
            return outcome;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
        {
            string? why;
            lock (gate)
            {
                why = cut;
            }

            return outcome ?? "socket:cut:" + (why ?? (aborted.IsCancellationRequested ? "gone" : lifetime.IsCancellationRequested ? "deadline" : "broken"));
        }
        finally
        {
            if (socket.State != WebSocketState.Closed)
            {
                socket.Abort();
            }

            if (!receiving.IsCompleted)
            {
                lifetime.Cancel();
            }

            await receiving;
            fromLodestone.Writer.TryComplete(new IOException("The session ended."));
        }
    }

    /// <summary>
    /// Reads <paramref name="lodestoneId"/>'s page through the plugin's connection: a piped read's
    /// place, then <c>open</c>, <c>opened</c> within its deadline, and the fetch within its own from
    /// <c>open</c>; then <c>close</c>, whatever happened. Only one pipe ever opens on a WebSocket.
    /// </summary>
    public async Task<LodestoneRead> ReadAsync(long lodestoneId, CancellationToken cancellation)
    {
        using var place = reads.TryTake();
        if (place is null)
        {
            return new LodestoneRead(LodestoneOutcome.Busy);
        }

        lock (gate)
        {
            if (stage != Stage.Waiting)
            {
                throw new InvalidOperationException("A WebSocket opens one pipe at most.");
            }

            stage = Stage.Opening;
            openSent = true;
        }

        LodestoneRead read;
        using (var fetch = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            fetch.CancelAfter(limits.FetchDeadline);
            try
            {
                await SendAsync(OpenMessage, WebSocketMessageType.Text);
                read = await WaitOpenedAsync(fetch.Token, cancellation)
                    ? await pages.ReadAsync(lodestoneId, new PipeStream(this), fetch.Token, cancellation)
                    : new LodestoneRead(LodestoneOutcome.Unanswered);
            }
            finally
            {
                lock (gate)
                {
                    if (stage is Stage.Opening or Stage.Open)
                    {
                        stage = Stage.Closed;
                    }
                }

                fromLodestone.Writer.TryComplete(new IOException("The pipe is closed."));
            }
        }

        await SendAsync(CloseMessage, WebSocketMessageType.Text);
        return read;
    }

    public void Dispose()
    {
        stop.Dispose();
        sending.Dispose();
    }

    /// <summary>The first message: binary, the signed body within its bound, within its deadline. Null when it isn't, with why in <see cref="cut"/>.</summary>
    private async Task<byte[]?> ReceiveFirstAsync()
    {
        using var first = CancellationTokenSource.CreateLinkedTokenSource(session);
        first.CancelAfter(limits.FirstMessageDeadline);
        var buffer = new byte[SignedRequests.MaxActionRequestBytes + 1];
        try
        {
            var (type, count) = await ReceiveMessageAsync(buffer, first.Token);
            if (type != WebSocketMessageType.Binary || count > SignedRequests.MaxActionRequestBytes)
            {
                cut = type == WebSocketMessageType.Close ? "closed" : type != WebSocketMessageType.Binary ? "first-message-type" : "first-message-size";
                return null;
            }

            return buffer.AsSpan(0, count).ToArray();
        }
        catch (OperationCanceledException) when (!session.IsCancellationRequested)
        {
            cut = "first-message-late";
            return null;
        }
    }

    /// <summary>Every message after the first, until the plugin's close or the session's end.</summary>
    private async Task ReceiveAsync()
    {
        var buffer = new byte[LodestoneSockets.MaxMessageBytes + 1];
        try
        {
            while (true)
            {
                var (type, count) = await ReceiveMessageAsync(buffer, session);
                string? unexpected;
                if (type == WebSocketMessageType.Close)
                {
                    lock (gate)
                    {
                        if (stage == Stage.Finished)
                        {
                            return;
                        }
                    }

                    unexpected = "closed";
                }
                else
                {
                    unexpected = count > LodestoneSockets.MaxMessageBytes ? "message-size" : Take(type, buffer.AsSpan(0, count));
                }

                if (unexpected is not null)
                {
                    End(unexpected);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection broke, or the WebSocket can't be read any more.
            End("broken");
        }
    }

    /// <summary>
    /// One message the plugin sent after its first, applied to the exchange. Null when the exchange
    /// expects it; otherwise why it ends the session.
    /// </summary>
    private string? Take(WebSocketMessageType type, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (stage == Stage.Waiting)
            {
                return "unexpected-message";
            }

            if (type == WebSocketMessageType.Binary)
            {
                if (opening != Opening.Opened || lodestoneEnded)
                {
                    return "unexpected-bytes";
                }

                bytesFromLodestone += bytes.Length;
                if (bytesFromLodestone > LodestoneSockets.MaxBytesFromLodestone)
                {
                    return "bytes-from-lodestone";
                }

                if (stage == Stage.Open && bytes.Length > 0)
                {
                    fromLodestone.Writer.TryWrite(bytes.ToArray());
                }

                return null;
            }

            if (openSent && opening == Opening.Unanswered && (bytes.SequenceEqual("opened"u8) || bytes.SequenceEqual("failed"u8)))
            {
                // A late answer, after "close", is dropped.
                opening = bytes[0] == (byte)'o' ? Opening.Opened : Opening.Failed;
                if (stage == Stage.Opening)
                {
                    if (opening == Opening.Opened)
                    {
                        stage = Stage.Open;
                    }

                    answered.TrySetResult(opening == Opening.Opened);
                }

                return null;
            }

            if (opening == Opening.Opened && !lodestoneEnded && bytes.SequenceEqual("eof"u8))
            {
                lodestoneEnded = true;
                fromLodestone.Writer.TryComplete();
                return null;
            }

            return "unexpected-message";
        }
    }

    /// <summary>Ends the session at once: whatever reads the pipe sees the session's end, and the WebSocket is dropped.</summary>
    private void End(string why)
    {
        lock (gate)
        {
            cut ??= why;
        }

        stop.Cancel();
        fromLodestone.Writer.TryComplete(new IOException("The session ended."));
    }

    /// <summary>Whether the plugin answered <c>opened</c> within its deadline; false for <c>failed</c> or no answer.</summary>
    private async Task<bool> WaitOpenedAsync(CancellationToken fetch, CancellationToken cancellation)
    {
        using var opened = CancellationTokenSource.CreateLinkedTokenSource(fetch);
        opened.CancelAfter(limits.OpenedDeadline);
        try
        {
            return await answered.Task.WaitAsync(opened.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Sends the server's bytes toward the Lodestone, within the pipe's total, in messages of at most 64 KiB.</summary>
    private async Task SendToLodestoneAsync(ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (stage != Stage.Open)
            {
                throw new IOException("The pipe is closed.");
            }

            bytesToLodestone += bytes.Length;
            if (bytesToLodestone > LodestoneSockets.MaxBytesToLodestone)
            {
                throw new IOException("The pipe's bytes toward the Lodestone are spent.");
            }
        }

        for (var offset = 0; offset < bytes.Length; offset += LodestoneSockets.MaxMessageBytes)
        {
            await SendAsync(bytes.Slice(offset, Math.Min(LodestoneSockets.MaxMessageBytes, bytes.Length - offset)), WebSocketMessageType.Binary);
        }
    }

    /// <summary>
    /// Sends one whole message, one at a time. Only the session's end cancels a send, since a
    /// cancelled send drops the WebSocket, and the session still owes its final message.
    /// </summary>
    private async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type)
    {
        await sending.WaitAsync(session);
        try
        {
            await socket.SendAsync(bytes, type, endOfMessage: true, session);
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>One whole message, at most <paramref name="buffer"/>'s length: a longer one fills it, and its caller refuses it.</summary>
    private async Task<(WebSocketMessageType Type, int Count)> ReceiveMessageAsync(byte[] buffer, CancellationToken cancellation)
    {
        var count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, 0);
            }

            count += result.Count;
            if (result.EndOfMessage || count == buffer.Length)
            {
                return (result.MessageType, count);
            }
        }
    }

    /// <summary>The pipe as the fetch's handler sees it: the plugin's connection, carried in binary messages.</summary>
    private sealed class PipeStream(PipeSession owner) : Stream
    {
        private byte[] chunk = [];
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var reader = owner.fromLodestone.Reader;
            if (offset == chunk.Length)
            {
                // A read of no bytes waits until there are some, or the end, and reads none.
                if (buffer.IsEmpty)
                {
                    await reader.WaitToReadAsync(cancellationToken);
                    return 0;
                }

                while (true)
                {
                    if (!await reader.WaitToReadAsync(cancellationToken))
                    {
                        return 0;
                    }

                    if (reader.TryRead(out var next))
                    {
                        chunk = next;
                        offset = 0;
                        break;
                    }
                }
            }

            var count = Math.Min(buffer.Length, chunk.Length - offset);
            chunk.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>The fetch's own deadline doesn't cancel a send (<see cref="SendAsync"/>); the session's end does.</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            buffer.IsEmpty ? ValueTask.CompletedTask : new ValueTask(owner.SendToLodestoneAsync(buffer));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// The handler disposes the stream to end a request it cancels, without cancelling a read in
        /// progress, so a read waiting for the plugin's next bytes ends here.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            owner.fromLodestone.Writer.TryComplete(new IOException("The pipe is closed."));
            base.Dispose(disposing);
        }
    }
}
