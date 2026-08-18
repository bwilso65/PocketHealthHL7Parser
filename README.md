# HL7 ORU Ingestion Server

Receives HL7 v2 `ORU^R01` radiology reports over HTTP, stores **every** payload raw in SQLite and acknowledges it
immediately, then — asynchronously — validates each message and extracts the patient / order / report content from
the ones that pass. Built for Woodbine Health first, with a seam for the next providers' quirks.

C# / .NET 10 · ASP.NET Core minimal API · [HL7-V2](https://github.com/Efferent-Health/HL7-V2) parser ·
Microsoft.Data.Sqlite + Dapper · xUnit. See [PLAN.md](PLAN.md) for the plan/prompt log and full decision log.

## Run

```bash
docker compose up --build
```

The server listens on `http://localhost:8080`. The database is `./data/messages.db` (bind-mounted, WAL mode).

## Test

```bash
# Unit + integration tests (67), inside Docker
docker compose --profile test run --rm tests

# Post every sample message: prints the ACK for each, then each message's verdict from GET /messages/{id}
scripts/send-samples.sh            # or: scripts/send-samples.ps1
scripts/send-samples.sh --json     # JSON receipts instead of HL7 ACKs

# See what landed (uses the sqlite3 CLI inside the container)
scripts/show-db.sh
```

Or by hand — send one, then look it up (the `X-Message-Id` / `Location` header on the POST tells you where):

```bash
curl -i -X POST http://localhost:8080/messages -H "Content-Type: text/plain" --data-binary @samples/02_oru_valid_01.hl7
curl -s http://localhost:8080/messages/1              # status (received → accepted/duplicate/rejected) + patient + report
curl -s http://localhost:8080/messages/1/raw          # the exact bytes we received
curl -s "http://localhost:8080/messages?controlId=MSG00001"          # by the sender's control ID (MSH-10)
curl -s "http://localhost:8080/messages?status=rejected&limit=20"    # what's in quarantine
curl -s http://localhost:8080/healthz                                # {"status":"ok","pending":0}  ← queue depth
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
 receive (sync, per request)                     process (async, one background worker, FIFO)
 ─────────────────────────────                   ──────────────────────────────────────────────────────────────────────
 POST /messages ─▶ store raw bytes ─▶ 200 + ACK   ─▶ decode ─▶ parse ─▶ validate envelope ─▶ extract ─▶ validate content ─▶ verdict
                   status = received  "Received"     (charset)  (syntax)  (1 msg, ORU^R01,      (PID/OBR/     (required fields,     accepted
                   + wake the worker                                       known sender)          OBX)          OBX-11 present)       duplicate
                                                                                                                                       rejected
```

**Response contract** — the receiver's promise is *receipt*, not validity ("we either have the file or we don't"):

| HTTP | Meaning | Body |
|---|---|---|
| `200` | Your bytes are durably stored and queued. Nothing about the *content* changes this. | HL7 ACK: `MSA\|AA\|<your control id>\|Received` — or a JSON receipt with `Accept: application/json` |
| `400` | Nothing to store (empty body). | text |
| `5xx` | We could not store it — retry. | — |

The verdict is reached by the worker (normally within milliseconds) and is visible at `GET /messages/{id}`, in the
`messages` table, and in the logs. Every request lands in `messages` with `status ∈ {received, accepted, duplicate,
rejected, failed}`, the raw bytes, and a reason. Accepted messages additionally produce `reports` (one per OBR, with
the patient snapshot and the newline-joined report text) and `observations` (one per OBX).
Schema: [Schema.cs](src/Hl7Receiver/Storage/Schema.cs).

The `messages` table **is** the queue: `status = received` rows are pending, the worker sweeps them on startup
(so nothing received before a crash/restart is lost), wakes on each new receipt, and re-sweeps every 30 s as a safety
net. `GET /healthz` reports the queue depth.

**Reading it back** (JSON):

| Endpoint | Returns |
|---|---|
| `GET /messages/{id}` | status (`received` while queued), rejection reason, duplicate-of, `processedAt`, + the extracted reports: patient, procedure, report text, observations |
| `GET /messages/{id}/raw` | the exact bytes received — inspect a quarantined message |
| `GET /messages?controlId=&facility=&status=&limit=` | search, newest first; `controlId` is what the sender knows (MSH-10) and is only unique per `facility` |
| `GET /healthz` | liveness + `pending` queue depth |

### Behaviour for the sample messages

Every one of these gets `200` + `MSA|AA|…|Received` at POST time. The interesting column is the verdict:

| Sample | Verdict (`messages.status`) | Why |
|---|---|---|
| 01 minimal, 02 full, 03 other sender | accepted | 03 shows the sender is data (MSH-4), not a hard-coded "WOODBINE" |
| 04 same control ID as 02, different body | duplicate → points at #2 | Idempotent: the stored report is untouched; the body-hash mismatch is recorded in `detail` and logged as a warning |
| 05 broken MSH | rejected `UNPARSEABLE` | Can't parse; sender/facility still sniffed from the partial MSH at receipt; raw kept |
| 06 two messages in one payload | rejected `MULTIPLE_MSH` | One request = one message. Splitting would hide the sender's bug; taking only the first would silently drop a report. Raw kept, replayable |
| 07 truncated mid-OBX | rejected `REQUIRED_FIELD_MISSING` (OBX-11) | Result status is HL7-required and is the truncation tell. A partial radiology report stored as complete is a patient-safety problem |
| 08 ADT^A01 | rejected `UNSUPPORTED_MESSAGE_TYPE` | Not a report. Raw kept in case ADT becomes in-scope |

Leniency we chose: `\r`, `\n`, `\r\n` segment terminators; any HL7 2.x version; unknown segments (ORC, NTE, PV1,
Z-segments) ignored; multiple OBR per message supported; repeated PID-3 uses the first repetition; `\.br\`, `\F\`
etc. decoded; charset from MSH-18, else strict UTF-8, else ISO-8859-1 fallback (never silently corrupts accents).

## Decisions and Tradeoffs

**1. Receipt and verdict are separate: `200 + AA` the moment the bytes are stored; validation runs asynchronously.**
Woodbine's sender retries on non-2xx (per Maya). A malformed message is a *permanent* failure; answering `4xx` would
make their engine retry it forever. So the receiver's contract is deliberately narrow — *we either have the file or we
don't* — and everything about content happens afterwards, in a background worker fed from the `messages` table.
Two things fall out: the receiving code is trivially simple and hard to break, and a burst from one provider (their
queue backing up, per Maya) can't slow down *receipt* for anyone else. The trade-off, stated plainly: **the sender
never learns about a rejection through the ACK** — a bad message becomes our operational problem (quarantine,
`GET /messages?status=rejected`, warning-level logs, and, with more time, alerting), not something their engine
error-queues. Given Maya couldn't confirm they even read the ACK, and given that immediate-AA-then-process is what
interface engines do by default, that is acceptable — but it should be confirmed with Woodbine (see assumptions).
The ACK code is `AA`, not HL7's more precise `CA` (commit accept), because `CA` only exists in enhanced-ack mode and
Woodbine's messages are original mode, where the sender expects `AA`. Rejected: synchronous validation with the
verdict in the ACK (`AE`/`AR` + `ERR`) — the first version of this service, still in git history (`d2a41b1`); more
informative for the sender, but couples receipt to processing and lets a burst delay other providers' 200s.
Rejected: honest REST (`400/422` for content errors) — wrong for a retrying sender.

**2. Store every payload raw first; the `messages` table is the queue, the audit trail, the quarantine, and the
idempotency ledger. Idempotency by (facility, app, control ID).**
One table, one row per request, one `status` column: `received` is the work queue (durable across restarts — the
worker sweeps it on startup), `rejected`/`failed` is the quarantine (bytes kept, replayable after a parser fix or a
conversation with Woodbine), `accepted`/`duplicate` is the ledger. No broker to run, no second store to keep
consistent, and the demo can *see* the queue with a `SELECT`. A single FIFO worker keeps per-sender ordering (a
correction must not overtake its original) and SQLite is single-writer anyway. Duplicates are keyed by sender *and*
control ID because two providers can both send `MSG00001`; only *accepted* messages participate, so a corrected
re-send of a previously rejected message is accepted; verdict writes are guarded by `status = 'received'` so a
verdict is recorded exactly once. Rejected: parse-then-store (a bug in our parser could lose data); an external
queue (Redis/RabbitMQ) — real leverage only past SQLite's ceiling, which 500/day isn't near; control-ID-only keys;
overwriting on duplicate (04's differing body is exactly the case where overwriting is dangerous, so we keep the
original and flag the mismatch). Known limit: a burst from provider A delays *processing* (not receipt) of provider
B's messages behind it; per-facility worker lanes are the next step if that ever matters.

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

Smaller calls: SQLite in WAL mode with
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
| An immediate `AA` ("received") is acceptable to Woodbine, i.e. they do **not** need the validation verdict in the ACK and are OK with rejections being surfaced by us (`GET /messages`, alerts) rather than error-queued on their side | Maya couldn't say | Re-introduce synchronous validation with `AE`/`AR` in the ACK for that provider (the first version of this service; git `d2a41b1`) — a per-provider option off `ProviderProfile` |
| Original-mode acknowledgement (MSH-15/16 empty, as in the samples) | samples | If they run enhanced mode, answer `CA` instead of `AA` |
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
- **Operations.** Metrics (received/accepted/rejected/duplicate/failed per provider, queue depth, processing
  latency), alerting on rejection spikes and on `pending` growing, a replay path for quarantined/failed messages
  (`POST /messages/{id}/replay` = set status back to `received`), structured JSON logs with correlation IDs,
  OpenTelemetry. With async processing, this is what tells Woodbine's ops about bad messages — it matters more now.
- **Amendments and report lifecycle.** Preliminary → final → corrected (OBX-11 `P`/`F`/`C`, OBR-25). Today every
  message yields new report rows; add a "latest per (facility, accession)" view or an explicit supersedes link.
- **Per-provider processing lanes.** One worker per sending facility (ordering preserved within a provider), so a
  burst from one provider can't delay processing for another. Receipt is already isolated; this is about the verdict
  latency. Also a per-provider "sync verdict in the ACK" option for engines that error-queue on `AE`/`AR`.
- **Graceful drain on shutdown / a proper poison-message policy** — today an in-flight message is left `received`
  and picked up on restart; a message that throws is marked `failed` after one attempt (no retries, no backoff).
- **Report-centric reads.** Today's API is message-centric (what was sent, what happened). Add
  `GET /reports?accession=…&patientId=…&facility=…` for the PocketHealth-side question ("this patient's reports"),
  with paging.
- **Provider profiles from configuration/DB** instead of code, with per-provider validation policies and a small
  admin surface. Contract test suites per provider from real de-identified samples.
- **Embedded documents.** Radiology reports often arrive as base64 PDFs in an `ED` OBX; store them as blobs/files.
- **Schema migrations** (DbUp/EF migrations) once the schema starts changing under real data. Retention/purge
  policy for raw payloads (PHI).

## Notes for the Reviewer

- Layout: `src/Hl7Receiver/{Hl7,Ingestion,Storage,Http}`; `Program.cs` is just wiring. Read it in this order:
  `Http/MessagesEndpoint` → `Ingestion/MessageReceiver` (the hot path) → `Ingestion/ProcessingWorker` →
  `Ingestion/MessageProcessor` (the pipeline) → `Hl7/*` (parser wrapper, extractor, validator, provider profile) →
  `Storage/*`. Tests are in `tests/Hl7Receiver.Tests` — the eight samples are copied in as fixtures and drive
  [`SampleBehaviorTests`](tests/Hl7Receiver.Tests/SampleBehaviorTests.cs); `EndpointTests` covers the leniency and
  the provider seam; `AsyncProcessingTests` covers receipt-before-verdict, restart recovery, bursts and FIFO;
  `ReadEndpointTests` covers the GET API; `UnitTests` covers the ACK builder, MSH sniffing, timestamps.
- Tests run the real background worker; helpers poll `messages.status` until the verdict lands (milliseconds).
- Built and tested entirely inside Docker (no local SDK); what you run is what I ran.
- AI use (Claude Code) is described in [PLAN.md](PLAN.md), including what was deliberately not delegated.
- The container runs as root so the `./data` bind mount works on any host without permission games; a hardened
  image would use the `app` user and a named volume.
