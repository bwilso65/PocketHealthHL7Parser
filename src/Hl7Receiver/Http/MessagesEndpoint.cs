using System.Text;
using Hl7Receiver.Ingestion;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Http;

/// <summary>
/// POST /messages — one HL7 v2 message per request, raw in the body.
///
/// HTTP status is the *transport/commit* signal, the ACK body is the *application* verdict:
///   200  we have durably stored your bytes; look at MSA-1 (AA accepted / AE error / AR reject) for the outcome
///   400  there was nothing to store (empty body)
///   5xx  we could not store it — retry
/// Woodbine's sender retries on non-2xx, so a permanently-bad message must NOT get a 4xx (it would retry forever);
/// it gets 200 + AE/AR, is quarantined in the DB, and is visible in logs/queries. Same split as HL7's own
/// commit-ack vs application-ack.
///
/// Response body: HL7 ACK (text/plain) by default; JSON with <c>Accept: application/json</c> for humans and tooling.
/// </summary>
public static class MessagesEndpoint
{
    public static IEndpointRouteBuilder MapMessagesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/messages", async (HttpRequest request, HttpResponse response, IngestionService ingestion, CancellationToken ct) =>
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

            var result = ingestion.Ingest(body);
            response.Headers["X-Message-Id"] = result.MessageId.ToString();

            if (WantsJson(request))
            {
                return Results.Json(new
                {
                    messageId = result.MessageId,
                    status = result.Status.ToString().ToLowerInvariant(),
                    ackCode = result.AckCode.ToString(),
                    sender = new { application = result.Header.SendingApplication, facility = result.Header.SendingFacility },
                    messageControlId = result.Header.MessageControlId,
                    messageType = result.Header.MessageType,
                    reports = result.ReportCount,
                    duplicateOf = result.DuplicateOf,
                    payloadDiffersFromOriginal = result.Status == MessageStatus.Duplicate ? result.PayloadDiffersFromOriginal : (bool?)null,
                    rejection = result.Rejection is null ? null : new { code = result.Rejection.Code, hl7ErrorCode = result.Rejection.Hl7ErrorCode, detail = result.Rejection.Detail },
                }, statusCode: StatusCodes.Status200OK);
            }

            return Results.Text(result.Ack, "text/plain", Encoding.UTF8, StatusCodes.Status200OK);
        })
        .WithName("PostMessage")
        .Accepts<string>("text/plain", "application/hl7-v2", "x-application/hl7-v2+er7", "application/octet-stream");

        return app;
    }

    private static bool IsBlank(byte[] body) =>
        body.All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');

    private static bool WantsJson(HttpRequest request) =>
        request.Headers.Accept.Any(v => v is not null && v.Contains("application/json", StringComparison.OrdinalIgnoreCase));
}
