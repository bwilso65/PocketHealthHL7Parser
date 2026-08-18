# HL7 ORU Ingestion Server

Receives HL7 v2 `ORU^R01` radiology reports over HTTP, stores **every** payload raw in SQLite, extracts the
patient / order / report content from the ones that pass validation, and answers the sender with an HL7 ACK.
Built for Woodbine Health first, with a seam for the next providers' quirks.

C# / .NET 10 · ASP.NET Core minimal API · [HL7-V2](https://github.com/Efferent-Health/HL7-V2) parser ·
Microsoft.Data.Sqlite + Dapper · xUnit. See [PLAN.md](PLAN.md) for the plan/prompt log and full decision log.

## Run

```bash
docker compose up --build
```

The server listens on `http://localhost:8080`. The database is `./data/messages.db` (bind-mounted, WAL mode).

## Test

```bash
# Unit + integration tests (61), inside Docker
docker compose --profile test run --rm tests

# Post every sample message and print the ACK for each
scripts/send-samples.sh            # or: scripts/send-samples.ps1
scripts/send-samples.sh --json     # JSON summaries instead of HL7 ACKs

# See what landed (uses the sqlite3 CLI inside the container)
scripts/show-db.sh
```

Or by hand — send one, then look it up (the `X-Message-Id` / `Location` header on the POST tells you where):

```bash
curl -i -X POST http://localhost:8080/messages -H "Content-Type: text/plain" --data-binary @samples/02_oru_valid_01.hl7
curl -s http://localhost:8080/messages/1              # outcome + patient + report + observations, as JSON
curl -s http://localhost:8080/messages/1/raw          # the exact bytes we received
curl -s "http://localhost:8080/messages?controlId=MSG00001"          # by the sender's control ID (MSH-10)
curl -s "http://localhost:8080/messages?status=rejected&limit=20"    # what's in quarantine
```

Or straight from SQLite:

```bash
docker compose exec hl7-server sqlite3 -header -column /app/data/messages.db \
  "SELECT id, sending_facility, message_control_id, status, rejection_code FROM messages;"
docker compose exec hl7-server sqlite3 -header -column /app/data/messages.db \
  "SELECT accession_number, patient_identifier, patient_family_name, procedure_description, report_text FROM reports;"
```

## What it does

```
POST /messages  ──▶ decode bytes ──▶ sniff MSH ──▶ parse ──▶ validate envelope ──▶ extract ──▶ validate content ──▶ persist ──▶ ACK
                    (charset)        (best-effort) (syntax)  (1 msg, ORU^R01,      (PID/OBR/OBX)  (required fields,    (SQLite,
                                                              known sender)                         OBX-11 present)      idempotent)
```

**Response contract** — HTTP status is the *commit* signal; the ACK body is the *application* verdict:

| HTTP | Meaning | Body |
|---|---|---|
| `200` | We have durably stored your bytes. Look at `MSA-1`: `AA` accepted, `AE` content error, `AR` rejected. | HL7 ACK (`MSH`, `MSA`, `ERR` on error) — or JSON with `Accept: application/json` |
| `400` | Nothing to store (empty body). | text |
| `5xx` | We could not store it — retry. | — |

Every request lands in `messages` with `status ∈ {accepted, duplicate, rejected}`, the raw bytes, and a reason.
Accepted messages additionally produce `reports` (one per OBR, with the patient snapshot and the newline-joined
report text) and `observations` (one per OBX). Schema: [Schema.cs](src/Hl7Receiver/Storage/Schema.cs).

**Reading it back** (JSON):

| Endpoint | Returns |
|---|---|
| `GET /messages/{id}` | outcome (status, rejection reason, duplicate-of) + the extracted reports: patient, procedure, report text, observations |
| `GET /messages/{id}/raw` | the exact bytes received — inspect a quarantined message |
| `GET /messages?controlId=&facility=&status=&limit=` | search, newest first; `controlId` is what the sender knows (MSH-10) and is only unique per `facility` |
| `GET /healthz` | liveness |

### Behaviour for the sample messages

| Sample | HTTP | ACK | `messages.status` | Why |
|---|---|---|---|---|
| 01 minimal, 02 full, 03 other sender | 200 | AA | accepted | 03 shows the sender is data (MSH-4), not a hard-coded "WOODBINE" |
| 04 same control ID as 02, different body | 200 | AA | duplicate → points at #2 | Idempotent: the retry succeeds so the sender stops; the stored report is untouched; the body-hash mismatch is recorded and logged as a warning |
| 05 broken MSH | 200 | AR | rejected `UNPARSEABLE` | Can't parse; sender/facility still sniffed from the partial MSH; raw kept |
| 06 two messages in one payload | 200 | AR | rejected `MULTIPLE_MSH` | One request = one message = one ACK. Splitting would hide the sender's bug; taking only the first would silently drop a report. Raw kept, replayable |
| 07 truncated mid-OBX | 200 | AE | rejected `REQUIRED_FIELD_MISSING` (OBX-11) | Result status is HL7-required and is the truncation tell. A partial radiology report stored as complete is a patient-safety problem |
| 08 ADT^A01 | 200 | AR | rejected `UNSUPPORTED_MESSAGE_TYPE` | Not a report. Raw kept in case ADT becomes in-scope |

Leniency we chose: `\r`, `\n`, `\r\n` segment terminators; any HL7 2.x version; unknown segments (ORC, NTE, PV1,
Z-segments) ignored; multiple OBR per message supported; repeated PID-3 uses the first repetition; `\.br\`, `\F\`
etc. decoded; charset from MSH-18, else strict UTF-8, else ISO-8859-1 fallback (never silently corrupts accents).

## Decisions and Tradeoffs

**1. `200 + AE/AR` for bad-but-stored messages, not `4xx`.**
Woodbine's sender retries on non-2xx (per Maya). A malformed message is a *permanent* failure; answering `422`
would make their engine retry it forever, and every retry would hit us and be rejected again. So the HTTP status
only says "we have your bytes" (or "we don't — retry"), and the ACK code carries the verdict — the same split HL7
itself makes between commit-level and application-level acknowledgements, and what HL7-over-HTTP implementations
do. Rejected: honest REST semantics (`400/422` for rejects). It reads better in isolation but is wrong for this
sender. The cost: an operator has to look at `MSA-1` / the DB / logs to see rejections, not just at status codes —
so those are all made obvious.

**2. Store every payload raw first; a rejection is a quarantine, not a drop. Idempotency by (facility, app, control ID).**
The `messages` table is the audit trail, the dead-letter queue, and the idempotency ledger in one. That is what
makes the strict choices above safe: rejecting 06 or 07 loses nothing — the bytes are there to inspect and replay
after a parser fix or a conversation with Woodbine. Duplicates are keyed by sender *and* control ID because two
providers can both send `MSG00001`. Only *accepted* messages participate in the key, so a corrected re-send of a
previously rejected message is accepted. Rejected: parse-then-store (a bug in our parser could lose data);
control-ID-only keys; overwriting on duplicate (04's differing body is exactly the case where overwriting is
dangerous, so we keep the original and flag the mismatch).

**3. A schema-free HL7 library plus our own policy layer, with a per-provider profile seam.**
[HL7-V2](https://www.nuget.org/packages/HL7-V2) (MIT, no dependencies) does the tokenizing, delimiter handling,
escape decoding, and terminator tolerance, and enforces only syntax (starts with `MSH`, MSH-9/10/11/12 present,
well-formed segment names). Everything that is a *judgment* — which message types, which fields are required, what
truncation looks like, one-message-per-request — lives in [`OruValidator`](src/Hl7Receiver/Hl7/OruValidator.cs)
and [`OruExtractor`](src/Hl7Receiver/Hl7/OruExtractor.cs), driven by a
[`ProviderProfile`](src/Hl7Receiver/Hl7/ProviderProfile.cs) (validation policy + field mapping) resolved by MSH-4.
Woodbine uses the default profile; the next provider that puts the accession number somewhere else, or needs a
looser rule, gets an override entry and nothing else changes (there's a test showing this). Rejected: nHapi — a
faithful HAPI port with generated per-version models; strict and heavy for a receiver that needs ~15 fields and has
to tolerate quirks. Rejected: hand-rolled parser — fine for these eight files, grows fast (repetitions,
sub-components, escapes, Z-segments), and every bug is ours.

Smaller calls: synchronous processing (parse + persist is sub-millisecond; 50–500 msgs/day with bursts is far
below SQLite's single-writer ceiling; the ACK reflects what actually happened); SQLite in WAL mode with
`CREATE IF NOT EXISTS` at startup (no migration tool yet); **normalized tables, not a JSON blob** — `messages` /
`reports` / `observations` are what you'd query on day one ("reports for this patient", "rejections from this
provider this week"), the raw bytes are kept alongside for anything the schema doesn't capture, and a denormalized
`report_text` on `reports` serves the obvious read; timestamps stored as ISO-8601 with the precision sent and **no
invented timezone**; PID demographics stored as a snapshot on the report (no patient master — identity matching is
downstream's job); a read API keyed by **our** message id (what the POST hands back) *and* searchable by the
**sender's** control ID (what Woodbine's ops would quote us) — because both conversations happen.

## Assumptions to confirm with Woodbine before go-live

Maya's assistant could not answer these; the defaults below are documented in code and easy to change.

| Assumption | Where | If wrong |
|---|---|---|
| Transport is HTTP POST, one message per request | scaffold / Maya | Add an MLLP listener over the same pipeline (see below) |
| Accession number = OBR-3.1 (filler order number) | `ProviderProfile.Default` | Change one `FieldRef` |
| Patient identifier = PID-3.1 (first repetition, MRN) | `ProviderProfile.Default` | Same |
| A standard HL7 ACK is expected; sender retries on non-2xx | Maya | Response mapping is one class (`MessagesEndpoint`) |
| Woodbine's engine *reads* the ACK (`MSA-1`/`ERR`) and someone monitors `AE`/`AR` — otherwise our rejections are invisible to them | Maya couldn't say | Add rejection alerting on our side and/or a notification path to their ops (see below) |
| Their engine treats a `200 + AE/AR` as "delivered, don't retry" and error-queues it | Maya couldn't say | If it retries on non-AA, duplicates of rejected messages are harmless here (they're not deduplicated) but noisy |
| Encoding UTF-8 unless MSH-18 says otherwise | `PayloadDecoder` | Add the charset to the switch |
| Timestamps are Eastern (Ontario) but sent without offset | stored as-is | Apply the offset at read time / add a per-provider TZ |
| Only `ORU^R01`; ADT is out of scope | `ValidationPolicy.Default` | Add the type to `AcceptedMessageTypes` and an extractor |
| Corrections/addenda arrive as new messages (new control ID); every version is kept | data model | Add a "current report per accession" view or an amendment status |
| Sender identity is trusted from MSH-3/4 (no auth on the endpoint) | — | mTLS / API key / IP allowlist before any real PHI flows |

## What I'd Do With More Time

- **MLLP listener.** Most hospital engines speak MLLP/TCP, not HTTP. `IngestionService` is transport-agnostic and
  the library already has MLLP frame extraction; a TCP listener that frames with `0x0B ... 0x1C 0x0D` and writes the
  ACK back is a contained addition.
- **Security for PHI.** TLS termination, mTLS or API keys per provider, IP allowlist, sender allowlist (reject
  unknown MSH-4), request size limits, non-root container user, secrets management, audit of who read what.
- **Operations.** Metrics (accepted/rejected/duplicate per provider), alerting on rejection spikes, a replay
  path for quarantined messages (`POST /messages/{id}/replay`), structured JSON logs with correlation IDs,
  OpenTelemetry.
- **Amendments and report lifecycle.** Preliminary → final → corrected (OBX-11 `P`/`F`/`C`, OBR-25). Today every
  message yields new report rows; add a "latest per (facility, accession)" view or an explicit supersedes link.
- **Async option for bursts.** Persist raw and answer `200` immediately, process from the `messages` table in a
  background worker (it is already shaped like a queue). Not needed at 500/day, cheap to add if latency SLOs appear.
- **Report-centric reads.** Today's API is message-centric (what was sent, what happened). Add
  `GET /reports?accession=…&patientId=…&facility=…` for the PocketHealth-side question ("this patient's reports"),
  with paging.
- **Provider profiles from configuration/DB** instead of code, with per-provider validation policies and a small
  admin surface. Contract test suites per provider from real de-identified samples.
- **Embedded documents.** Radiology reports often arrive as base64 PDFs in an `ED` OBX; store them as blobs/files.
- **Schema migrations** (DbUp/EF migrations) once the schema starts changing under real data. Retention/purge
  policy for raw payloads (PHI).

## Notes for the Reviewer

- Layout: `src/Hl7Receiver/{Hl7,Ingestion,Storage,Http}`; `Program.cs` is just wiring. Tests are in
  `tests/Hl7Receiver.Tests` — the eight samples are copied in as fixtures and drive
  [`SampleBehaviorTests`](tests/Hl7Receiver.Tests/SampleBehaviorTests.cs); `EndpointTests` covers the leniency and
  the provider seam; `ReadEndpointTests` covers the GET API; `UnitTests` covers the ACK builder, MSH sniffing,
  timestamps.
- Built and tested entirely inside Docker (no local SDK); what you run is what I ran.
- AI use (Claude Code) is described in [PLAN.md](PLAN.md), including what was deliberately not delegated.
- The container runs as root so the `./data` bind mount works on any host without permission games; a hardened
  image would use the `app` user and a named volume.
