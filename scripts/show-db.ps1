# Shows what landed in SQLite, using the sqlite3 CLI that ships inside the container.
# Usage: .\scripts\show-db.ps1

function Invoke-Sql {
    param([string] $Query)
    # Queries are kept on one line: docker.exe rebuilds the command line for the container on Windows,
    # and an embedded newline inside a quoted argument gets mis-split, so multi-line SQL breaks here.
    & docker compose exec -T hl7-server sqlite3 -header -column /app/data/messages.db $Query
}

Write-Host "== messages: every payload received, with the ACK we sent and the outcome (status=queued: reports not written yet) =="
Invoke-Sql "SELECT id, substr(received_at, 12, 12) AS received, substr(processed_at, 12, 12) AS processed, sending_facility AS facility, message_control_id AS control_id, message_type AS type, ack_code AS ack, status, rejection_code, duplicate_of, substr(detail, 1, 45) AS detail FROM messages ORDER BY id;"

Write-Host ""
Write-Host "== reports: here's the patient, here's the report =="
Invoke-Sql "SELECT r.id, r.sending_facility AS facility, r.accession_number AS accession, r.patient_identifier AS mrn, r.patient_family_name || ', ' || r.patient_given_name AS patient, r.patient_date_of_birth AS dob, r.procedure_description AS procedure, r.observation_datetime AS observed_at FROM reports r ORDER BY r.id;"

Write-Host ""
Write-Host "== report text =="
Invoke-Sql "SELECT r.accession_number AS accession, r.report_text FROM reports r ORDER BY r.id;"

Write-Host ""
Write-Host "== observations (one row per OBX) =="
Invoke-Sql "SELECT o.report_id, o.set_id, o.value_type AS type, o.result_status AS status, substr(o.value, 1, 60) AS value FROM observations o ORDER BY o.report_id, o.set_id;"
