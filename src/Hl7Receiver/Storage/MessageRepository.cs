using System.Data;
using Dapper;
using Hl7Receiver.Hl7;
using Microsoft.Data.Sqlite;

namespace Hl7Receiver.Storage;

public enum MessageStatus
{
    /// <summary>Validated and ACKed AA; reports not yet written (the work queue).</summary>
    Queued,
    /// <summary>Reports written. Terminal.</summary>
    Accepted,
    /// <summary>Same (sender, control id) as a live message; ACKed AA, not reprocessed. Terminal.</summary>
    Duplicate,
    /// <summary>Failed validation at receipt; ACKed AE/AR. Terminal, kept for inspection/replay.</summary>
    Rejected,
    /// <summary>Something threw (validation at receipt, or extraction in the worker). Kept for replay. Terminal.</summary>
    Failed,
}

/// <summary>Everything we know about a payload the moment it arrives. Stored regardless of what happens next.</summary>
public sealed record MessageReceipt(DateTimeOffset ReceivedAt, byte[] Raw, string Sha256, MessageHeader Header);

/// <summary>What the receiver decided synchronously, before the row is written.</summary>
public abstract record ReceiptVerdict
{
    public sealed record Valid : ReceiptVerdict;
    public sealed record Rejected(Rejection Rejection) : ReceiptVerdict;
}

/// <summary>The row as stored at receipt.</summary>
public sealed record StoredReceipt(long MessageId, MessageStatus Status, AckCode AckCode, long? DuplicateOf = null, bool PayloadDiffersFromOriginal = false);

/// <summary>A queued message handed to the worker.</summary>
public sealed record PendingMessage(long Id, byte[] Raw, string Sha256);

/// <summary>
/// All writes go through here. Each public method is one SQLite transaction.
/// The receiver only ever calls <see cref="StoreReceipt"/>; everything else is the worker's.
/// </summary>
public sealed class MessageRepository(Database database)
{
    // ---- receipt (the hot path) ------------------------------------------------------------------

    /// <summary>
    /// Stores the raw payload with its receipt-time verdict and returns the row. This is the "we have your bytes"
    /// moment — the ACK is not sent until this commits.
    /// For a valid message, checks idempotency inside the same BEGIN IMMEDIATE transaction (SQLite has a single
    /// writer, so two concurrent retries cannot both become queued): if a live message with the same
    /// (sender, control id) exists, this one is stored as <c>duplicate</c> instead. The partial unique index is the backstop.
    /// </summary>
    public StoredReceipt StoreReceipt(MessageReceipt receipt, ReceiptVerdict verdict)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);

        StoredReceipt result;
        switch (verdict)
        {
            case ReceiptVerdict.Rejected r:
            {
                var status = r.Rejection.Code == "PROCESSING_ERROR" ? MessageStatus.Failed : MessageStatus.Rejected;
                var id = Insert(connection, tx, receipt, status, r.Rejection.Ack, r.Rejection.Code, r.Rejection.Detail, duplicateOf: null, terminal: true);
                result = new StoredReceipt(id, status, r.Rejection.Ack);
                break;
            }
            case ReceiptVerdict.Valid:
            {
                var h = receipt.Header;
                var original = connection.QuerySingleOrDefault<LiveRow>(
                    """
                    SELECT id AS Id, raw_sha256 AS Sha256 FROM messages
                    WHERE status IN ('queued', 'accepted')
                      AND sending_facility = @Facility
                      AND IFNULL(sending_application, '') = IFNULL(@Application, '')
                      AND message_control_id = @ControlId
                    """,
                    new { Facility = h.SendingFacility, Application = h.SendingApplication, ControlId = h.MessageControlId },
                    tx);

                if (original is not null)
                {
                    var differs = !string.Equals(original.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase);
                    var note = differs
                        ? $"Duplicate of message #{original.Id}; payload DIFFERS from the original (not reprocessed)"
                        : $"Duplicate of message #{original.Id}; identical payload (not reprocessed)";
                    var id = Insert(connection, tx, receipt, MessageStatus.Duplicate, AckCode.AA, rejectionCode: null, note, original.Id, terminal: true);
                    result = new StoredReceipt(id, MessageStatus.Duplicate, AckCode.AA, original.Id, differs);
                }
                else
                {
                    var id = Insert(connection, tx, receipt, MessageStatus.Queued, AckCode.AA, rejectionCode: null, detail: null, duplicateOf: null, terminal: false);
                    result = new StoredReceipt(id, MessageStatus.Queued, AckCode.AA);
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(verdict));
        }

        tx.Commit();
        return result;
    }

    private static long Insert(SqliteConnection connection, SqliteTransaction tx, MessageReceipt receipt, MessageStatus status,
        AckCode ackCode, string? rejectionCode, string? detail, long? duplicateOf, bool terminal)
    {
        var h = receipt.Header;
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO messages (
                received_at, processed_at, sending_application, sending_facility, message_control_id, message_type,
                processing_id, hl7_version, status, ack_code, rejection_code, detail, duplicate_of, raw_message, raw_sha256)
            VALUES (
                @ReceivedAt, @ProcessedAt, @SendingApplication, @SendingFacility, @MessageControlId, @MessageType,
                @ProcessingId, @Hl7Version, @Status, @AckCode, @RejectionCode, @Detail, @DuplicateOf, @Raw, @Sha256);
            SELECT last_insert_rowid();
            """,
            new
            {
                ReceivedAt = Iso(receipt.ReceivedAt),
                ProcessedAt = terminal ? Iso(receipt.ReceivedAt) : null,
                h.SendingApplication,
                h.SendingFacility,
                h.MessageControlId,
                h.MessageType,
                h.ProcessingId,
                Hl7Version = h.VersionId,
                Status = StatusText(status),
                AckCode = ackCode.ToString(),
                RejectionCode = rejectionCode,
                Detail = detail,
                DuplicateOf = duplicateOf,
                receipt.Raw,
                receipt.Sha256,
            },
            tx);
    }

    // ---- queue ---------------------------------------------------------------------------------------

    /// <summary>Oldest queued message ids, in receipt order (FIFO — preserves per-sender ordering).</summary>
    public IReadOnlyList<long> NextPending(int limit)
    {
        using var connection = database.Open();
        return connection.Query<long>(
            "SELECT id FROM messages WHERE status = 'queued' ORDER BY id LIMIT @Limit", new { Limit = limit }).ToList();
    }

    public int CountPending()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM messages WHERE status = 'queued'");
    }

    /// <summary>The raw payload of a message that is still queued, or null if it isn't (already processed / unknown).</summary>
    public PendingMessage? LoadPending(long id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<PendingMessage>(
            "SELECT id AS Id, raw_message AS Raw, raw_sha256 AS Sha256 FROM messages WHERE id = @Id AND status = 'queued'",
            new { Id = id });
    }

    // ---- completion (the worker) ---------------------------------------------------------------------

    /// <summary>
    /// Writes the extracted reports/observations and moves the message from queued to accepted.
    /// Returns false if the message was no longer queued (someone else completed it) — nothing is written in that case.
    /// </summary>
    public bool MarkAccepted(long id, MessageHeader header, OruMessage oru, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);

        if (!SetTerminal(connection, tx, id, MessageStatus.Accepted, rejectionCode: null, detail: null, now))
        {
            return false;
        }

        var createdAt = Iso(now);
        foreach (var report in oru.Reports)
        {
            var reportId = connection.ExecuteScalar<long>(
                """
                INSERT INTO reports (
                    message_id, sequence, sending_facility, accession_number, placer_order_number,
                    procedure_code, procedure_description, procedure_coding_system, observation_datetime, result_status,
                    patient_identifier, patient_identifier_authority, patient_identifier_type,
                    patient_family_name, patient_given_name, patient_middle_name, patient_date_of_birth, patient_sex,
                    report_text, message_datetime, created_at)
                VALUES (
                    @MessageId, @Sequence, @SendingFacility, @AccessionNumber, @PlacerOrderNumber,
                    @ProcedureCode, @ProcedureDescription, @ProcedureCodingSystem, @ObservationDateTime, @ResultStatus,
                    @PatientIdentifier, @PatientIdentifierAuthority, @PatientIdentifierType,
                    @PatientFamilyName, @PatientGivenName, @PatientMiddleName, @PatientDateOfBirth, @PatientSex,
                    @ReportText, @MessageDateTime, @CreatedAt);
                SELECT last_insert_rowid();
                """,
                new
                {
                    MessageId = id,
                    report.Sequence,
                    SendingFacility = header.SendingFacility!,
                    report.AccessionNumber,
                    report.PlacerOrderNumber,
                    report.ProcedureCode,
                    report.ProcedureDescription,
                    report.ProcedureCodingSystem,
                    ObservationDateTime = Hl7Timestamp.ToIso8601(report.ObservationDateTime),
                    report.ResultStatus,
                    PatientIdentifier = oru.Patient.Identifier,
                    PatientIdentifierAuthority = oru.Patient.IdentifierAssigningAuthority,
                    PatientIdentifierType = oru.Patient.IdentifierTypeCode,
                    PatientFamilyName = oru.Patient.FamilyName,
                    PatientGivenName = oru.Patient.GivenName,
                    PatientMiddleName = oru.Patient.MiddleName,
                    PatientDateOfBirth = Hl7Timestamp.ToIso8601(oru.Patient.DateOfBirth),
                    PatientSex = oru.Patient.Sex,
                    report.ReportText,
                    MessageDateTime = Hl7Timestamp.ToIso8601(header.MessageDateTime),
                    CreatedAt = createdAt,
                },
                tx);

            foreach (var obx in report.Observations)
            {
                connection.Execute(
                    """
                    INSERT INTO observations (report_id, set_id, value_type, identifier, identifier_text, value, units, result_status)
                    VALUES (@ReportId, @SetId, @ValueType, @Identifier, @IdentifierText, @Value, @Units, @ResultStatus)
                    """,
                    new { ReportId = reportId, obx.SetId, obx.ValueType, obx.Identifier, obx.IdentifierText, obx.Value, obx.Units, obx.ResultStatus },
                    tx);
            }
        }

        tx.Commit();
        return true;
    }

    /// <returns>false if the message was no longer queued.</returns>
    public bool MarkFailed(long id, string error, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        var done = SetTerminal(connection, tx, id, MessageStatus.Failed, rejectionCode: "PROCESSING_ERROR", detail: error, now);
        tx.Commit();
        return done;
    }

    /// <summary>
    /// Moves a message from 'queued' to a terminal status. Guarded by <c>status = 'queued'</c> so the transition happens
    /// exactly once even if two drainers ever race on the same row; returns false if someone got there first.
    /// </summary>
    private static bool SetTerminal(SqliteConnection connection, SqliteTransaction tx, long id, MessageStatus status,
        string? rejectionCode, string? detail, DateTimeOffset now)
    {
        var updated = connection.Execute(
            """
            UPDATE messages
            SET status = @Status, processed_at = @ProcessedAt, rejection_code = @RejectionCode, detail = @Detail
            WHERE id = @Id AND status = 'queued'
            """,
            new { Id = id, Status = StatusText(status), ProcessedAt = Iso(now), RejectionCode = rejectionCode, Detail = detail },
            tx);

        return updated == 1;
    }

    private static string StatusText(MessageStatus status) => status.ToString().ToLowerInvariant();

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O");

    private sealed class LiveRow
    {
        public long Id { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
