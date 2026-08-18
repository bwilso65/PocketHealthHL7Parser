#!/usr/bin/env bash
# Shows what landed in SQLite, using the sqlite3 CLI that ships inside the container.
# Usage: scripts/show-db.sh
set -euo pipefail

q() {
  docker compose exec -T hl7-server sqlite3 -header -column /app/data/messages.db "$1"
}

echo "== messages: every payload received, with outcome =="
q "SELECT id, received_at, sending_facility AS facility, message_control_id AS control_id, message_type AS type,
          status, rejection_code, duplicate_of, substr(detail, 1, 60) AS detail
   FROM messages ORDER BY id;"

echo
echo "== reports: here's the patient, here's the report =="
q "SELECT r.id, r.sending_facility AS facility, r.accession_number AS accession, r.patient_identifier AS mrn,
          r.patient_family_name || ', ' || r.patient_given_name AS patient, r.patient_date_of_birth AS dob,
          r.procedure_description AS procedure, r.observation_datetime AS observed_at
   FROM reports r ORDER BY r.id;"

echo
echo "== report text =="
q "SELECT r.accession_number AS accession, r.report_text FROM reports r ORDER BY r.id;"

echo
echo "== observations (one row per OBX) =="
q "SELECT o.report_id, o.set_id, o.value_type AS type, o.result_status AS status, substr(o.value, 1, 60) AS value
   FROM observations o ORDER BY o.report_id, o.set_id;"
