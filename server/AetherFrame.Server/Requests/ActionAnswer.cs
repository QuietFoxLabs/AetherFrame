using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AetherFrame.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace AetherFrame.Server.Requests;

/// <summary>
/// What a signed action answers, whether its body came as a <c>POST</c> or as a WebSocket's first
/// message (ServerApi-v1.md, sections 2.1 and 2.3): a status, the kind the request log records, and
/// at most one of a JSON body, a fresh challenge (a <c>409</c>) or a reason (a <c>503</c> when the
/// Lodestone turned the player's own connection away). A successful check or re-read also carries
/// <paramref name="ReadDay"/>: the stored day of the answered binding's last successful Lodestone
/// read, in UTC days since the Unix epoch. A <c>POST</c> gets the answer exactly as the endpoints
/// answered before the WebSocket mode, never with the day; a WebSocket gets it as its final message.
/// </summary>
internal sealed record ActionAnswer(int Status, string? Kind, object? Json = null, byte[]? Challenge = null, string? Reason = null, long? ReadDay = null)
{
    /// <summary>A status with no body, its kind left for the request log.</summary>
    public static ActionAnswer Fail(int status, string kind) => new(status, kind);

    /// <summary>A <c>200</c> with <paramref name="json"/> as its body.</summary>
    public static ActionAnswer Ok(object json) => new(StatusCodes.Status200OK, null, json);

    /// <summary>
    /// The answer as a <c>POST</c> gets it: a JSON body, the 32 challenge bytes with a <c>409</c>, or
    /// the status alone, with the kind left for the request log.
    /// </summary>
    public IResult ToResult(HttpContext http)
    {
        if (Kind is not null)
        {
            http.Items[SignedRequests.ErrorKindItem] = Kind;
        }

        if (Challenge is not null)
        {
            return new BytesWithStatus(Status, Challenge);
        }

        return Json is not null ? Results.Json(Json, ServerJson.Options) : Results.StatusCode(Status);
    }

    /// <summary>
    /// The answer as a WebSocket's final message (ServerApi-v1.md, section 2.3): one JSON object with
    /// <c>status</c>, and <c>body</c> (the JSON a <c>POST</c> gets) with <c>readDay</c> beside it for
    /// a successful check or re-read, <c>challenge</c> (the fresh challenge, in base64) or
    /// <c>reason</c> when the answer has one.
    /// </summary>
    public byte[] ToFinalMessage()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status", Status);
            if (Json is not null)
            {
                writer.WritePropertyName("body");
                JsonSerializer.Serialize(writer, Json, Json.GetType(), ServerJson.Options);
            }

            if (ReadDay is { } readDay)
            {
                writer.WriteNumber("readDay", readDay);
            }

            if (Challenge is not null)
            {
                writer.WriteString("challenge", Convert.ToBase64String(Challenge));
            }

            if (Reason is not null)
            {
                writer.WriteString("reason", Reason);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>A binary body with a status other than 200.</summary>
    private sealed class BytesWithStatus(int status, byte[] bytes) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/octet-stream";
            httpContext.Response.ContentLength = bytes.Length;
            return httpContext.Response.Body.WriteAsync(bytes).AsTask();
        }
    }
}
