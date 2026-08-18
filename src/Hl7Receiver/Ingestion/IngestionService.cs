using System.Security.Cryptography;
using Hl7Receiver.Hl7;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// The pipeline: decode → sniff header → parse → validate envelope → extract → validate content → persist → ACK.
/// Every payload ends up in the <c>messages</c> table with a status; this method only throws if persistence itself
/// fails (DB unavailable) — which the HTTP layer turns into a 5xx so the sender retries.
/// </summary>
public sealed class IngestionService(
    Hl7Parser parser,
    OruExtractor extractor,
    OruValidator validator,
    IProviderProfileRegistry profiles,
    MessageRepository repository,
    AckBuilder acks,
    TimeProvider clock,
    ILogger<IngestionService> logger)
{
    public IngestionResult Ingest(byte[] payload)
    {
        var receivedAt = clock.GetUtcNow();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        var decoded = PayloadDecoder.Decode(payload);
        var header = MessageHeader.Sniff(decoded.Text);
        var receipt = new MessageReceipt(receivedAt, payload, sha256, header);
        var profile = profiles.For(header.SendingFacility);

        if (decoded.UsedFallback)
        {
            logger.LogWarning("Payload from {Facility} controlId={ControlId} was not valid UTF-8; decoded as {Charset}",
                header.SendingFacility, header.MessageControlId, decoded.Charset);
        }

        var (oru, rejection) = Process(decoded.Text, header, profile);

        IngestionResult result;
        if (rejection is not null)
        {
            var outcome = repository.PersistRejected(receipt, rejection);
            result = new IngestionResult(outcome.MessageId, MessageStatus.Rejected, rejection.Ack, acks.Reject(header, rejection),
                header, rejection, null, false, 0);
        }
        else
        {
            var outcome = repository.PersistAccepted(receipt, oru!);
            var note = outcome.Status == MessageStatus.Duplicate
                ? "Duplicate of a previously accepted message; not reprocessed"
                : null;
            result = new IngestionResult(outcome.MessageId, outcome.Status, AckCode.AA, acks.Accept(header, note),
                header, null, outcome.DuplicateOf, outcome.PayloadDiffersFromOriginal, outcome.ReportCount);
        }

        Log(result, profile);
        return result;
    }

    private (OruMessage? Oru, Rejection? Rejection) Process(string text, MessageHeader header, ProviderProfile profile)
    {
        var parsed = parser.Parse(text);
        if (!parsed.Success)
        {
            return (null, parsed.Rejection);
        }

        var envelope = validator.ValidateEnvelope(parsed.Message!, header, profile.Policy);
        if (envelope is not null)
        {
            return (null, envelope);
        }

        var extracted = extractor.Extract(parsed.Message!, header, profile);
        if (!extracted.Success)
        {
            return (null, extracted.Rejection);
        }

        var content = validator.ValidateContent(extracted.Value!, profile.Policy);
        return content is not null ? (null, content) : (extracted.Value, null);
    }

    private void Log(IngestionResult r, ProviderProfile profile)
    {
        var level = r.Status switch
        {
            MessageStatus.Rejected => LogLevel.Warning,
            MessageStatus.Duplicate when r.PayloadDiffersFromOriginal => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        logger.Log(level,
            "HL7 {Status} id={MessageId} sender={Facility}/{Application} controlId={ControlId} type={MessageType} ack={Ack} profile={Profile} reports={Reports}{Detail}",
            r.Status, r.MessageId, r.Header.SendingFacility, r.Header.SendingApplication, r.Header.MessageControlId,
            r.Header.MessageType, r.AckCode, profile.Name, r.ReportCount,
            r.Rejection is not null ? $" reason={r.Rejection.Code}: {r.Rejection.Detail}"
                : r.DuplicateOf is not null ? $" duplicateOf={r.DuplicateOf}{(r.PayloadDiffersFromOriginal ? " PAYLOAD-DIFFERS" : "")}"
                : string.Empty);
    }
}
