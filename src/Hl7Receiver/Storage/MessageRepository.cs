using System.Data;
using Dapper;
using Hl7Receiver.Hl7;
using Microsoft.Data.Sqlite;

namespace Hl7Receiver.Storage;

public enum MessageStatus
{
    Accepted,
    Duplicate,
    Rejected,
}

/// <summary>Everything we know about a payload before it is parsed. Stored regardless of outcome.</summary>
public sealed record MessageReceipt(DateTimeOffset ReceivedAt, byte[] Raw, string Sha256, MessageHeader Header);

/// <summary>Result of persisting a message.</summary>
/// <param name="MessageId">Row id in <c>messages</c> for this receipt (also for duplicates and rejections).</param>
/// <param name="DuplicateOf">For duplicates: the row id of the accepted original.</param>
/// <param name="PayloadDiffersFromOriginal">For duplicates: the retry's bytes differ from what we accepted (sender bug worth flagging).</param>
public sealed record PersistOutcome(long MessageId, MessageStatus Status, long? DuplicateOf = null, bool PayloadDiffersFromOriginal = false, int ReportCount = 0);

/// <summary>All writes go through here. Each public method is one SQLite transaction.</summary>
public sealed class MessageRepository(Database database)
{
    /// <summary>
    /// Stores an accepted message and its reports, unless a message with the same (sender, control id) has already
    /// been accepted — in which case only a <c>duplicate</c> receipt is written and the original is left untouched.
    /// SQLite has a single writer, and <see cref="IsolationLevel.Serializable"/> maps to BEGIN IMMEDIATE, so the
    /// check-then-insert is race-free; the partial unique index is the backstop.
    /// </summary>
    public PersistOutcome PersistAccepted(MessageReceipt receipt, OruMessage oru)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);

        var header = receipt.Header;
        var original = connection.QuerySingleOrDefault<AcceptedRow>(
            """
            SELECT id AS Id, raw_sha256 AS Sha256 FROM messages
            WHERE status = 'accepted'
              AND sending_facility = @Facility
              AND IFNULL(sending_application, '') = IFNULL(@Application, '')
              AND message_control_id = @ControlId
            """,
            new { Facility = header.SendingFacility, Application = header.SendingApplication, ControlId = header.MessageControlId },
            tx);

        if (original is not null)
        {
            var differs = !string.Equals(original.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase);
            var note = differs
                ? $"Duplicate of message #{original.Id}; payload DIFFERS from the accepted original (not reprocessed)"
                : $"Duplicate of message #{original.Id}; identical payload (not reprocessed)";

            var duplicateId = InsertMessage(connection, tx, receipt, MessageStatus.Duplicate, rejectionCode: null, detail: note, duplicateOf: original.Id);
            tx.Commit();
            return new PersistOutcome(duplicateId, MessageStatus.Duplicate, original.Id, differs);
        }

        var messageId = InsertMessage(connection, tx, receipt, MessageStatus.Accepted, rejectionCode: null, detail: null, duplicateOf: null);
        var now = receipt.ReceivedAt.UtcDateTime.ToString("O");

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
                    MessageId = messageId,
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
                    CreatedAt = now,
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
        return new PersistOutcome(messageId, MessageStatus.Accepted, ReportCount: oru.Reports.Count);
    }

    /// <summary>Stores a rejected payload (raw bytes + reason) so it can be inspected and replayed.</summary>
    public PersistOutcome PersistRejected(MessageReceipt receipt, Rejection rejection)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        var id = InsertMessage(connection, tx, receipt, MessageStatus.Rejected, rejection.Code, rejection.Detail, duplicateOf: null);
        tx.Commit();
        return new PersistOutcome(id, MessageStatus.Rejected);
    }

    private sealed class AcceptedRow
    {
        public long Id { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private static long InsertMessage(SqliteConnection connection, SqliteTransaction tx, MessageReceipt receipt,
        MessageStatus status, string? rejectionCode, string? detail, long? duplicateOf)
    {
        var h = receipt.Header;
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO messages (
                received_at, sending_application, sending_facility, message_control_id, message_type,
                processing_id, hl7_version, status, rejection_code, detail, duplicate_of, raw_message, raw_sha256)
            VALUES (
                @ReceivedAt, @SendingApplication, @SendingFacility, @MessageControlId, @MessageType,
                @ProcessingId, @Hl7Version, @Status, @RejectionCode, @Detail, @DuplicateOf, @Raw, @Sha256);
            SELECT last_insert_rowid();
            """,
            new
            {
                ReceivedAt = receipt.ReceivedAt.UtcDateTime.ToString("O"),
                h.SendingApplication,
                h.SendingFacility,
                h.MessageControlId,
                h.MessageType,
                h.ProcessingId,
                Hl7Version = h.VersionId,
                Status = status.ToString().ToLowerInvariant(),
                RejectionCode = rejectionCode,
                Detail = detail,
                DuplicateOf = duplicateOf,
                receipt.Raw,
                receipt.Sha256,
            },
            tx);
    }
}
