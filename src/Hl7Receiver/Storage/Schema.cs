namespace Hl7Receiver.Storage;

/// <summary>
/// SQLite schema. Applied with CREATE ... IF NOT EXISTS on startup (no migration framework — see README).
///
/// Three tables:
///   messages      — every payload ever received, raw, with its verdict. Doubles as the durable work queue
///                   (status = queued: validated, ACKed AA, reports not yet written) and the quarantine
///                   (status = rejected / failed).
///   reports       — one row per OBR in an accepted message: the report a clinician/patient would look at
///   observations  — one row per OBX under a report (preserves structure; reports.report_text is the joined narrative)
///
/// Status lifecycle:
///   at receipt (synchronous, before the ACK):  queued | duplicate | rejected | failed(validation threw)
///   by the worker (asynchronous):              queued → accepted | failed
/// </summary>
public static class Schema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS messages (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            received_at         TEXT    NOT NULL,   -- ISO-8601 UTC, server clock, when the bytes were stored + ACKed
            processed_at        TEXT,               -- ISO-8601 UTC, when the terminal status was reached (NULL while queued)
            sending_application TEXT,               -- MSH-3   (best-effort from the raw header)
            sending_facility    TEXT,               -- MSH-4   (the provider)
            message_control_id  TEXT,               -- MSH-10
            message_type        TEXT,               -- MSH-9 as sent, e.g. ORU^R01
            processing_id       TEXT,               -- MSH-11 (P/T/D)
            hl7_version         TEXT,               -- MSH-12
            status              TEXT    NOT NULL,   -- queued | accepted | duplicate | rejected | failed
            ack_code            TEXT,               -- MSA-1 we returned: AA | AE | AR
            rejection_code      TEXT,               -- e.g. UNPARSEABLE, UNSUPPORTED_MESSAGE_TYPE (status = rejected/failed)
            detail              TEXT,               -- human-readable reason / note
            duplicate_of        INTEGER REFERENCES messages(id),  -- status = duplicate: the original
            raw_message         BLOB    NOT NULL,   -- exact bytes received
            raw_sha256          TEXT    NOT NULL
        );

        -- Idempotency: one live (queued or accepted) message per (sender, control id). Retries land as status = duplicate.
        -- Rejected/failed messages are not part of the key: a corrected re-send with the same control id can be accepted.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_live_identity
            ON messages (sending_facility, IFNULL(sending_application, ''), message_control_id)
            WHERE status IN ('queued', 'accepted');

        CREATE INDEX IF NOT EXISTS ix_messages_received_at ON messages (received_at);
        CREATE INDEX IF NOT EXISTS ix_messages_status      ON messages (status, id);   -- the worker's queue scan

        CREATE TABLE IF NOT EXISTS reports (
            id                          INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id                  INTEGER NOT NULL REFERENCES messages(id),
            sequence                    INTEGER NOT NULL,   -- position of the OBR within the message (1-based)
            sending_facility            TEXT    NOT NULL,   -- MSH-4, denormalized for querying by provider
            accession_number            TEXT,               -- OBR-3.1 by default (provider profile decides)
            placer_order_number         TEXT,               -- OBR-2.1
            procedure_code              TEXT,               -- OBR-4.1
            procedure_description       TEXT,               -- OBR-4.2
            procedure_coding_system     TEXT,               -- OBR-4.3
            observation_datetime        TEXT,               -- OBR-7, ISO-8601 (no offset unless the sender sent one)
            result_status               TEXT,               -- OBR-25
            patient_identifier          TEXT,               -- PID-3.1 (first repetition)
            patient_identifier_authority TEXT,              -- PID-3.4
            patient_identifier_type     TEXT,               -- PID-3.5, e.g. MR
            patient_family_name         TEXT,               -- PID-5.1
            patient_given_name          TEXT,               -- PID-5.2
            patient_middle_name         TEXT,               -- PID-5.3
            patient_date_of_birth       TEXT,               -- PID-7, ISO-8601 date
            patient_sex                 TEXT,               -- PID-8
            report_text                 TEXT    NOT NULL,   -- OBX-5 values in order, newline-joined
            message_datetime            TEXT,               -- MSH-7, ISO-8601
            created_at                  TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_reports_message   ON reports (message_id);
        CREATE INDEX IF NOT EXISTS ix_reports_accession ON reports (sending_facility, accession_number);
        CREATE INDEX IF NOT EXISTS ix_reports_patient   ON reports (sending_facility, patient_identifier);

        CREATE TABLE IF NOT EXISTS observations (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            report_id       INTEGER NOT NULL REFERENCES reports(id),
            set_id          INTEGER,            -- OBX-1
            value_type      TEXT,               -- OBX-2 (TX, FT, ST, ED, ...)
            identifier      TEXT,               -- OBX-3.1
            identifier_text TEXT,               -- OBX-3.2
            value           TEXT,               -- OBX-5, escapes decoded, HL7 line breaks as '\n'
            units           TEXT,               -- OBX-6.1
            result_status   TEXT                -- OBX-11 (F, P, C, ...)
        );

        CREATE INDEX IF NOT EXISTS ix_observations_report ON observations (report_id);
        """;
}
