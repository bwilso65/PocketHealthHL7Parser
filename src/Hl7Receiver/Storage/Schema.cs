namespace Hl7Receiver.Storage;

/// <summary>
/// SQLite schema. Applied with CREATE ... IF NOT EXISTS on startup (no migration framework — see README).
/// </summary>
public static class Schema
{
    public const string Sql = """
        -- Every payload the server has ever received, exactly as received, regardless of outcome.
        -- This is the audit trail and the quarantine: rejected/duplicate messages are never lost,
        -- and accepted ones can be re-parsed later if the extraction logic changes.
        CREATE TABLE IF NOT EXISTS messages (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            received_at         TEXT    NOT NULL,   -- ISO-8601, UTC, server clock
            sending_application TEXT,               -- MSH-3
            sending_facility    TEXT,               -- MSH-4
            message_control_id  TEXT,               -- MSH-10
            message_type        TEXT,               -- MSH-9, e.g. ORU^R01
            hl7_version         TEXT,               -- MSH-12
            status              TEXT    NOT NULL,   -- accepted | duplicate | rejected
            error_code          TEXT,               -- populated when status = rejected
            error_detail        TEXT,
            raw_message         BLOB    NOT NULL,   -- exact bytes received
            raw_sha256          TEXT    NOT NULL
        );

        -- Idempotency: one *accepted* message per (sender, control id). Retries land as status=duplicate.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_accepted_identity
            ON messages (sending_facility, sending_application, message_control_id)
            WHERE status = 'accepted';

        CREATE INDEX IF NOT EXISTS ix_messages_received_at ON messages (received_at);
        """;
}
