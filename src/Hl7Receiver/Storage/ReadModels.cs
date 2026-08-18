namespace Hl7Receiver.Storage;

// Read models returned by GET /messages... — shaped for a human or a downstream consumer, not for the DB.
// (JSON serialization uses camelCase: receivedAt, messageControlId, ...)

public sealed record MessageSummaryView(
    long Id,
    string ReceivedAt,
    string? ProcessedAt,           // null while status = queued
    string Status,                 // queued | accepted | duplicate | rejected | failed
    string? AckCode,               // MSA-1 we returned at receipt: AA | AE | AR
    SenderView Sender,
    string? MessageControlId,
    string? MessageType,
    string? ProcessingId,
    string? Hl7Version,
    RejectionView? Rejection,      // when rejected
    long? DuplicateOf,             // when duplicate: id of the accepted original
    string? Detail,                // human-readable note (rejection reason, duplicate note)
    string RawSha256,
    int ReportCount);

public sealed record MessageView(
    long Id,
    string ReceivedAt,
    string? ProcessedAt,
    string Status,
    string? AckCode,
    SenderView Sender,
    string? MessageControlId,
    string? MessageType,
    string? ProcessingId,
    string? Hl7Version,
    RejectionView? Rejection,
    long? DuplicateOf,
    string? Detail,
    string RawSha256,
    IReadOnlyList<ReportView> Reports);

public sealed record SenderView(string? Application, string? Facility);

public sealed record RejectionView(string Code, string Detail);

public sealed record ReportView(
    long Id,
    int Sequence,
    string? AccessionNumber,
    string? PlacerOrderNumber,
    ProcedureView Procedure,
    string? ObservationDateTime,
    string? ResultStatus,
    PatientView Patient,
    string ReportText,
    string? MessageDateTime,
    IReadOnlyList<ObservationView> Observations);

public sealed record ProcedureView(string? Code, string? Description, string? CodingSystem);

public sealed record PatientView(
    string? Identifier,
    string? AssigningAuthority,
    string? IdentifierType,
    string? FamilyName,
    string? GivenName,
    string? MiddleName,
    string? DateOfBirth,
    string? Sex);

public sealed record ObservationView(
    int? SetId,
    string? ValueType,
    string? Identifier,
    string? IdentifierText,
    string? Value,
    string? Units,
    string? ResultStatus);
