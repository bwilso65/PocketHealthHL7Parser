using Hl7Receiver.Hl7;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>What the processor decided about one message (for logging/tests; the DB row is the source of truth).</summary>
public sealed record ProcessingOutcome(long MessageId, MessageStatus Status, MessageHeader Header, Rejection? Rejection,
    long? DuplicateOf, bool PayloadDiffersFromOriginal, int ReportCount);

/// <summary>
/// The pipeline, run by the worker for one queued message:
/// decode → sniff header → parse → validate envelope → extract → validate content → record verdict.
/// Every message ends as accepted / duplicate / rejected. Only an unexpected exception escapes (the worker turns
/// that into <c>failed</c>).
/// </summary>
public sealed class MessageProcessor(
    Hl7Parser parser,
    OruExtractor extractor,
    OruValidator validator,
    IProviderProfileRegistry profiles,
    MessageRepository repository,
    TimeProvider clock,
    ILogger<MessageProcessor> logger)
{
    /// <returns>The outcome, or null if the message was not pending (already processed, or unknown).</returns>
    public ProcessingOutcome? Process(long messageId)
    {
        var pending = repository.LoadPending(messageId);
        if (pending is null)
        {
            return null;
        }

        var decoded = PayloadDecoder.Decode(pending.Raw);
        var header = MessageHeader.Sniff(decoded.Text);
        var profile = profiles.For(header.SendingFacility);

        if (decoded.UsedFallback)
        {
            logger.LogWarning("Message id={MessageId} from {Facility} was not valid UTF-8; decoded as {Charset}",
                messageId, header.SendingFacility, decoded.Charset);
        }

        var (oru, rejection) = Evaluate(decoded.Text, header, profile);

        ProcessingOutcome outcome;
        if (rejection is not null)
        {
            if (!repository.MarkRejected(messageId, rejection, clock.GetUtcNow()))
            {
                return null; // someone else reached a verdict first
            }
            outcome = new ProcessingOutcome(messageId, MessageStatus.Rejected, header, rejection, null, false, 0);
        }
        else
        {
            var persisted = repository.MarkAccepted(messageId, header, pending.Sha256, oru!, clock.GetUtcNow());
            if (persisted is null)
            {
                return null;
            }
            outcome = new ProcessingOutcome(messageId, persisted.Status, header, null, persisted.DuplicateOf,
                persisted.PayloadDiffersFromOriginal, persisted.ReportCount);
        }

        Log(outcome, profile);
        return outcome;
    }

    private (OruMessage? Oru, Rejection? Rejection) Evaluate(string text, MessageHeader header, ProviderProfile profile)
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

    private void Log(ProcessingOutcome r, ProviderProfile profile)
    {
        var level = r.Status switch
        {
            MessageStatus.Rejected => LogLevel.Warning,
            MessageStatus.Duplicate when r.PayloadDiffersFromOriginal => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        logger.Log(level,
            "HL7 {Status} id={MessageId} sender={Facility}/{Application} controlId={ControlId} type={MessageType} profile={Profile} reports={Reports}{Detail}",
            r.Status, r.MessageId, r.Header.SendingFacility, r.Header.SendingApplication, r.Header.MessageControlId,
            r.Header.MessageType, profile.Name, r.ReportCount,
            r.Rejection is not null ? $" reason={r.Rejection.Code}: {r.Rejection.Detail}"
                : r.DuplicateOf is not null ? $" duplicateOf={r.DuplicateOf}{(r.PayloadDiffersFromOriginal ? " PAYLOAD-DIFFERS" : "")}"
                : string.Empty);
    }
}
