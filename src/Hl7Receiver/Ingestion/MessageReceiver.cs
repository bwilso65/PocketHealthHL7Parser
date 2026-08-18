using System.Security.Cryptography;
using Hl7Receiver.Hl7;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>What the receiver hands back to the HTTP layer.</summary>
public sealed record Receipt(long MessageId, MessageStatus Status, AckCode AckCode, string Ack, MessageHeader Header,
    Rejection? Rejection, long? DuplicateOf, bool PayloadDiffersFromOriginal);

/// <summary>
/// The synchronous half. For every request: validate in memory (~1 ms), store the raw bytes with the verdict in a
/// single write, then — and only then — answer with an ACK that tells the sender the truth:
///   AA  valid → queued for the worker (reports are written asynchronously); or an idempotent duplicate
///   AE  understood, content not acceptable (required field missing, truncated, structural)
///   AR  can't/won't process (unparseable, unsupported type, two messages in one payload)
/// If validation itself throws (our bug, not the message's), the bytes are still stored (status = failed, replayable)
/// and the sender gets AE with HL7 error 207 "application internal error" — a retry would not help.
/// The receiver never returns before the row is committed, so a 200 always means "we have your bytes".
/// </summary>
public sealed class MessageReceiver(
    MessageEvaluator evaluator,
    MessageRepository repository,
    ProcessingQueue queue,
    AckBuilder acks,
    TimeProvider clock,
    ILogger<MessageReceiver> logger)
{
    public Receipt Receive(byte[] payload)
    {
        var receivedAt = clock.GetUtcNow();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        MessageHeader header;
        ReceiptVerdict verdict;
        Rejection? rejection = null;
        try
        {
            var evaluation = evaluator.Evaluate(payload);
            header = evaluation.Header;
            if (evaluation.Decoded.UsedFallback)
            {
                logger.LogWarning("Payload from {Facility} controlId={ControlId} was not valid UTF-8; decoded as {Charset}",
                    header.SendingFacility, header.MessageControlId, evaluation.Decoded.Charset);
            }
            rejection = evaluation.Rejection;
            verdict = rejection is null ? new ReceiptVerdict.Valid() : new ReceiptVerdict.Rejected(rejection);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation threw for an inbound payload ({Bytes} bytes); storing as failed", payload.Length);
            header = MessageHeader.Sniff(PayloadDecoder.Decode(payload).Text);
            rejection = Rejection.InternalError($"{ex.GetType().Name}: {ex.Message}");
            verdict = new ReceiptVerdict.Rejected(rejection);
        }

        var stored = repository.StoreReceipt(new MessageReceipt(receivedAt, payload, sha256, header), verdict);

        string ack;
        switch (stored.Status)
        {
            case MessageStatus.Queued:
                queue.Signal();
                ack = acks.Accept(header);
                break;
            case MessageStatus.Duplicate:
                ack = acks.Accept(header, "Duplicate of a previously accepted message; not reprocessed");
                break;
            default:
                ack = acks.Reject(header, rejection!);
                break;
        }

        Log(stored, header, rejection, payload.Length);
        return new Receipt(stored.MessageId, stored.Status, stored.AckCode, ack, header, rejection, stored.DuplicateOf, stored.PayloadDiffersFromOriginal);
    }

    private void Log(StoredReceipt stored, MessageHeader header, Rejection? rejection, int bytes)
    {
        var level = stored.Status switch
        {
            MessageStatus.Rejected or MessageStatus.Failed => LogLevel.Warning,
            MessageStatus.Duplicate when stored.PayloadDiffersFromOriginal => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        logger.Log(level,
            "HL7 received id={MessageId} {Status} ack={Ack} sender={Facility}/{Application} controlId={ControlId} type={MessageType} bytes={Bytes}{Detail}",
            stored.MessageId, stored.Status, stored.AckCode, header.SendingFacility, header.SendingApplication,
            header.MessageControlId, header.MessageType, bytes,
            rejection is not null ? $" reason={rejection.Code}: {rejection.Detail}"
                : stored.DuplicateOf is not null ? $" duplicateOf={stored.DuplicateOf}{(stored.PayloadDiffersFromOriginal ? " PAYLOAD-DIFFERS" : "")}"
                : string.Empty);
    }
}
