using System.Text;
using Hl7Receiver.Ingestion;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Http;

/// <summary>
/// POST /messages — one HL7 v2 message per request, raw in the body.
///
/// The receiver's contract is receipt, not validity — "we either have the file or we don't":
///   200  the bytes are durably stored and queued; body is an HL7 ACK (MSA-1 = AA, "Received")
///   400  there was nothing to store (empty body)
///   5xx  we could not store it — retry
/// Woodbine's sender retries on non-2xx, so nothing about the *content* of a message ever produces a 4xx.
/// Validation happens asynchronously (see ProcessingWorker); the verdict is at GET /messages/{id}.
///
/// Response body: HL7 ACK (text/plain) by default; JSON with <c>Accept: application/json</c> for humans and tooling.
///
/// GET /messages/{id}        — what happened to a message (received / accepted / duplicate / rejected / failed),
///                             plus the extracted report(s) once accepted
/// GET /messages/{id}/raw    — the exact bytes we received (inspect a quarantined message)
/// GET /messages?controlId=&amp;facility=&amp;status=&amp;limit=  — find messages by what the sender knows (MSH-10)
/// </summary>
public static class MessagesEndpoint
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapMessagesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/messages", async (HttpRequest request, HttpResponse response, MessageReceiver receiver, CancellationToken ct) =>
        {
            byte[] body;
            using (var buffer = new MemoryStream())
            {
                await request.Body.CopyToAsync(buffer, ct);
                body = buffer.ToArray();
            }

            if (IsBlank(body))
            {
                return Results.Text("Empty request body; expected an HL7 v2 message.\n", "text/plain", Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var receipt = receiver.Receive(body);
            response.Headers["X-Message-Id"] = receipt.MessageId.ToString();
            response.Headers.Location = $"/messages/{receipt.MessageId}";

            if (WantsJson(request))
            {
                return Results.Json(new
                {
                    messageId = receipt.MessageId,
                    status = "received",
                    ackCode = "AA",
                    sender = new { application = receipt.Header.SendingApplication, facility = receipt.Header.SendingFacility },
                    messageControlId = receipt.Header.MessageControlId,
                    messageType = receipt.Header.MessageType,
                    href = $"/messages/{receipt.MessageId}",
                }, statusCode: StatusCodes.Status200OK);
            }

            return Results.Text(receipt.Ack, "text/plain", Encoding.UTF8, StatusCodes.Status200OK);
        })
        .WithName("PostMessage")
        .Accepts<string>("text/plain", "application/hl7-v2", "x-application/hl7-v2+er7", "application/octet-stream");

        app.MapGet("/messages/{id:long}", (long id, MessageQueries queries) =>
            queries.GetById(id) is { } message ? Results.Json(message) : Results.NotFound())
            .WithName("GetMessage");

        app.MapGet("/messages/{id:long}/raw", (long id, MessageQueries queries) =>
            queries.GetRaw(id) is { } raw ? Results.Bytes(raw, "text/plain") : Results.NotFound())
            .WithName("GetMessageRaw");

        app.MapGet("/messages", (string? controlId, string? facility, string? status, int? limit, MessageQueries queries) =>
        {
            if (status is not null && !MessageQueries.Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Problem($"status must be one of: {string.Join(", ", MessageQueries.Statuses)}", statusCode: StatusCodes.Status400BadRequest);
            }

            var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            return Results.Json(queries.Search(controlId, facility, status?.ToLowerInvariant(), effectiveLimit));
        })
        .WithName("SearchMessages");

        return app;
    }

    private static bool IsBlank(byte[] body) =>
        body.All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');

    private static bool WantsJson(HttpRequest request) =>
        request.Headers.Accept.Any(v => v is not null && v.Contains("application/json", StringComparison.OrdinalIgnoreCase));
}
