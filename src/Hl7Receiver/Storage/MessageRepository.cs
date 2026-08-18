using System.Data;
using Dapper;
using Hl7Receiver.Hl7;
using Microsoft.Data.Sqlite;

namespace Hl7Receiver.Storage;

public enum MessageStatus
{
    /// <summary>Bytes stored, verdict pending (the queue).</summary>
    Received,
    Accepted,
    Duplicate,
    Rejected,
    /// <summary>Processing threw (bug, storage error). Kept for replay; never silently dropped.</summary>
    Failed,
}

/// <summary>Everything we know about a payload the moment it arrives. Stored regardless of what happens next.</summary>
public sealed record MessageReceipt(DateTimeOffset ReceivedAt, byte[] Raw, string Sha256, MessageHeader Header);

/// <summary>A queued message handed to the processor.</summary>
public sealed record PendingMessage(long Id, byte[] Raw, string Sha256);

/// <summary>Result of reaching a verdict on a message.</summary>
/// <param name="DuplicateOf">For duplicates: the row id of the accepted original.</param>
/// <param name="PayloadDiffersFromOriginal">For duplicates: the retry's bytes differ from what we accepted (sender bug worth flagging).</param>
public sealed record PersistOutcome(MessageStatus Status, long? DuplicateOf = null, bool PayloadDiffersFromOriginal = false, int ReportCount = 0);

/// <summary>
/// All writes go through here. Each public method is one SQLite transaction.
/// The receiver only ever calls <see cref="InsertReceived"/>; everything else is the worker's.
/// </summary>
public sealed class MessageRepository(Database database)
{
    // ---- receipt (the hot path) ------------------------------------------------------------------

    /// <summary>Stores the raw payload with status = received and returns its id. This is the "we have your bytes" moment.</summary>
    public long InsertReceived(MessageReceipt receipt)
    {
        using var connection = database.Open();
        var h = receipt.Header;
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO messages (
                received_at, sending_application, sending_facility, message_control_id, message_type,
                processing_id, hl7_version, status, raw_message, raw_sha256)
            VALUES (
                @ReceivedAt, @SendingApplication, @SendingFacility, @MessageControlId, @MessageType,
                @ProcessingId, @Hl7Version, 'received', @Raw, @Sha256);
            SELECT last_insert_rowid();
            """,
            new
            {
                ReceivedAt = Iso(receipt.ReceivedAt),
                h.SendingApplication,
                h.SendingFacility,
                h.MessageControlId,
                h.MessageType,
                h.ProcessingId,
                Hl7Version = h.VersionId,
                receipt.Raw,
                receipt.Sha256,
            });
    }

    // ---- queue ---------------------------------------------------------------------------------------

    /// <summary>Oldest pending message ids, in receipt order (FIFO — preserves per-sender ordering).</summary>
    public IReadOnlyList<long> NextPending(int limit)
    {
        using var connection = database.Open();
        return connection.Query<long>(
            "SELECT id FROM messages WHERE status = 'received' ORDER BY id LIMIT @Limit", new { Limit = limit }).ToList();
    }

    public int CountPending()
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM messages WHERE status = 'received'");
    }

    /// <summary>The raw payload of a message that is still pending, or null if it isn't (already processed / unknown).</summary>
    public PendingMessage? LoadPending(long id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<PendingMessage>(
            "SELECT id AS Id, raw_message AS Raw, raw_sha256 AS Sha256 FROM messages WHERE id = @Id AND status = 'received'",
            new { Id = id });
    }

    // ---- verdicts (the worker) -----------------------------------------------------------------------

    /// <summary>
    /// Records an accepted message and its reports — unless a message with the same (sender, control id) has already
    /// been accepted, in which case this one is marked <c>duplicate</c> and the original is left untouched.
    /// <see cref="IsolationLevel.Serializable"/> maps to BEGIN IMMEDIATE, so the check-then-write is race-free; the
    /// partial unique index is the backstop. Returns null if the message was no longer pending (already processed).
    /// </summary>
    public PersistOutcome? MarkAccepted(long id, MessageHeader header, string sha256, OruMessage oru, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);

        var original = connection.QuerySingleOrDefault<AcceptedRow>(
            """
            SELECT id AS Id, raw_sha256 AS Sha256 FROM messages
            WHERE status = 'accepted'
              AND id <> @Id
              AND sending_facility = @Facility
              AND IFNULL(sending_application, '') = IFNULL(@Application, '')
              AND message_control_id = @ControlId
            """,
            new { Id = id, Facility = header.SendingFacility, Application = header.SendingApplication, ControlId = header.MessageControlId },
            tx);

        if (original is not null)
        {
            var differs = !string.Equals(original.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
            var note = differs
                ? $"Duplicate of message #{original.Id}; payload DIFFERS from the accepted original (not reprocessed)"
                : $"Duplicate of message #{original.Id}; identical payload (not reprocessed)";

            if (!SetVerdict(connection, tx, id, MessageStatus.Duplicate, rejectionCode: null, detail: note, duplicateOf: original.Id, now))
            {
                return null;
            }
            tx.Commit();
            return new PersistOutcome(MessageStatus.Duplicate, original.Id, differs);
        }

        if (!SetVerdict(connection, tx, id, MessageStatus.Accepted, rejectionCode: null, detail: null, duplicateOf: null, now))
        {
            return null;
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
        return new PersistOutcome(MessageStatus.Accepted, ReportCount: oru.Reports.Count);
    }

    /// <returns>false if the message was no longer pending.</returns>
    public bool MarkRejected(long id, Rejection rejection, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        var done = SetVerdict(connection, tx, id, MessageStatus.Rejected, rejection.Code, rejection.Detail, duplicateOf: null, now);
        tx.Commit();
        return done;
    }

    /// <returns>false if the message was no longer pending.</returns>
    public bool MarkFailed(long id, string error, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        var done = SetVerdict(connection, tx, id, MessageStatus.Failed, rejectionCode: "PROCESSING_ERROR", detail: error, duplicateOf: null, now);
        tx.Commit();
        return done;
    }

    /// <summary>
    /// Moves a message from 'received' to its verdict. Guarded by <c>status = 'received'</c> so a verdict is written
    /// exactly once even if two drainers ever race on the same row; returns false if someone got there first.
    /// </summary>
    private static bool SetVerdict(SqliteConnection connection, SqliteTransaction tx, long id, MessageStatus status,
        string? rejectionCode, string? detail, long? duplicateOf, DateTimeOffset now)
    {
        var updated = connection.Execute(
            """
            UPDATE messages
            SET status = @Status, processed_at = @ProcessedAt, rejection_code = @RejectionCode, detail = @Detail, duplicate_of = @DuplicateOf
            WHERE id = @Id AND status = 'received'
            """,
            new { Id = id, Status = status.ToString().ToLowerInvariant(), ProcessedAt = Iso(now), RejectionCode = rejectionCode, Detail = detail, DuplicateOf = duplicateOf },
            tx);

        return updated == 1;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O");

    private sealed class AcceptedRow
    {
        public long Id { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
