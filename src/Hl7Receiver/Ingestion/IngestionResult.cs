using Hl7Receiver.Hl7;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>What happened to one inbound payload. Everything the HTTP layer needs to answer the sender.</summary>
public sealed record IngestionResult(
    long MessageId,
    MessageStatus Status,
    AckCode AckCode,
    string Ack,                    // the HL7 ACK message to return
    MessageHeader Header,          // best-effort MSH fields (present even for rejections)
    Rejection? Rejection,          // when Status == Rejected
    long? DuplicateOf,             // when Status == Duplicate
    bool PayloadDiffersFromOriginal,
    int ReportCount);
