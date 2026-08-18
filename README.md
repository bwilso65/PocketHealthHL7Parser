# HL7 ORU Ingestion Server

Receives HL7 v2 `ORU^R01` radiology reports over HTTP. Every payload is validated and stored raw in SQLite before
the sender gets an honest HL7 ACK (`AA` / `AE` / `AR`); the patient / order / report content of accepted messages is
then extracted and written by a background worker. Built for Woodbine Health first, with a seam for the next
providers' quirks.

C# / .NET 10 · ASP.NET Core minimal API · [HL7-V2](https://github.com/Efferent-Health/HL7-V2) parser ·
Microsoft.Data.Sqlite + Dapper · xUnit. See [PLAN.md](PLAN.md) for the plan/prompt log and full decision log.

## Run

```bash
docker compose up --build
```

The server listens on `http://localhost:8080`. The database is `./data/messages.db` (bind-mounted, WAL mode).

## Test

```bash
# Unit + integration tests (69), inside Docker
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
curl -s http://localhost:8080/messages/1              # status (queued → accepted, or duplicate/rejected) + patient + report
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
 receive (synchronous, per request, ~1 ms)                                             process (async, one worker, FIFO)
 ───────────────────────────────────────────────────────────────────────────────────    ─────────────────────────────────
 POST /messages ─▶ decode ─▶ parse ─▶ validate ─▶ extract ─▶ validate ─▶ dup check ─▶ store raw ─▶ ACK    ─▶ re-extract ─▶ write reports
                   (charset)  (syntax)  envelope    (PID/OBR/   content     (facility+    + verdict    AA/AE/AR      from raw     status = accepted
                                        (1 msg,     OBX)        (required   app+ctrl id)  status =    (+ ERR)       bytes
                                        ORU^R01,                fields,                   queued /
                                        sender)                 OBX-11)                   rejected /
                                                                                          duplicate
```

**Response contract** — HTTP status is the *commit* signal; the ACK body is the *application* verdict:

| HTTP | Meaning | Body |
|---|---|---|
| `200` | We have durably stored your bytes. Look at `MSA-1`: `AA` valid → queued (or an idempotent duplicate); `AE` understood, content not acceptable; `AR` can't/won't process this kind of message. | HL7 ACK (`MSH`, `MSA`, `ERR` on error) — or a JSON receipt with `Accept: application/json` |
| `400` | Nothing to store (empty body). | text |
| `5xx` | We could not store it — retry. | — |

No ACK is sent before the row is committed, and the row carries the same verdict as the ACK. Every request lands
in `messages` with `status ∈ {queued, accepted, duplicate, rejected, failed}`, `ack_code`, the raw bytes, and a
reason. `AA` messages are `queued` until the worker writes their `reports` (one per OBR, with the patient snapshot
and the newline-joined report text) and `observations` (one per OBX) and marks them `accepted` — normally within
milliseconds. Schema: [Schema.cs](src/Hl7Receiver/Storage/Schema.cs).

The `messages` table **is** the work queue: `status = queued` rows are pending, the worker sweeps them on startup
(so nothing ACKed before a crash/restart is lost), wakes on each new receipt, and re-sweeps every 30 s as a safety
net. `GET /healthz` reports the queue depth. If validation itself throws (our bug), the bytes are still stored
(`failed`, replayable) and the sender gets `AE` with HL7 error 207 — a retry would not help.

**Reading it back** (JSON):

| Endpoint | Returns |
|---|---|
| `GET /messages/{id}` | status, the `ackCode` we returned, rejection reason, duplicate-of, `processedAt`, + the extracted reports: patient, procedure, report text, observations |
| `GET /messages/{id}/raw` | the exact bytes received — inspect a quarantined message |
| `GET /messages?controlId=&facility=&status=&limit=` | search, newest first; `controlId` is what the sender knows (MSH-10) and is only unique per `facility` |
| `GET /healthz` | liveness + `pending` queue depth |

### Behaviour for the sample messages

Every one of these gets HTTP `200` (the bytes were stored). The ACK carries the verdict:

| Sample | ACK | `messages.status` | Why |
|---|---|---|---|
| 01 minimal, 02 full, 03 other sender | AA | queued → accepted | 03 shows the sender is data (MSH-4), not a hard-coded "WOODBINE" |
| 04 same control ID as 02, different body | AA + "Duplicate…" | duplicate → points at #2 | Idempotent: the retry succeeds so the sender stops; the stored report is untouched; the body-hash mismatch is recorded in `detail` and logged as a warning |
| 05 broken MSH | AR + `ERR` 100 | rejected `UNPARSEABLE` | Can't parse; sender/facility still sniffed from the partial MSH; raw kept |
| 06 two messages in one payload | AR + `ERR` 100 | rejected `MULTIPLE_MSH` | One request = one message = one ACK. Splitting would hide the sender's bug; taking only the first would silently drop a report. Raw kept, replayable |
| 07 truncated mid-OBX | AE + `ERR` 101 | rejected `REQUIRED_FIELD_MISSING` (OBX-11) | Result status is HL7-required and is the truncation tell. A partial radiology report stored as complete is a patient-safety problem |
| 08 ADT^A01 | AR + `ERR` 200 | rejected `UNSUPPORTED_MESSAGE_TYPE` | Not a report. Raw kept in case ADT becomes in-scope |

`AE` vs `AR` follows HL7's intent: AR = "can't/won't process this kind of thing" (syntax, type, structure),
AE = "understood it, content isn't acceptable" (missing required field). The `ERR` segment carries a table-0357 code.

Leniency we chose: `\r`, `\n`, `\r\n` segment terminators; any HL7 2.x version; unknown segments (ORC, NTE, PV1,
Z-segments) ignored; multiple OBR per message supported; repeated PID-3 uses the first repetition; `\.br\`, `\F\`
etc. decoded; charset from MSH-18, else strict UTF-8, else ISO-8859-1 fallback (never silently corrupts accents).

## Decisions and Tradeoffs

**1. Validate synchronously and ACK honestly; write the reports asynchronously.**
Two facts drive the response contract. Woodbine's sender retries on non-2xx (per Maya), so a malformed message must
not get a `4xx` — it would be retried forever; the HTTP status therefore only says "we have your bytes" (`200`) or
"we don't — retry" (`5xx`), and the *ACK* carries the verdict. And an ACK is only worth sending if it's true, so
validation — parse, envelope, extraction, required fields, duplicate check — runs in the request, before the row is
written and before the ACK goes out. It's pure in-memory work, ~1 ms. What is deferred to the background worker is
the part that isn't needed for the verdict and is where heavier work will accumulate: writing `reports` /
`observations` (later: patient matching, embedded documents, notifications). So the sender gets `AE`/`AR` + `ERR`
for a bad message immediately and can error-queue it on their side, `AA` for a good one, and the reports appear
milliseconds later. History, because it's the honest story: the first version did everything synchronously
(`d2a41b1`); the second moved *all* processing behind the queue and ACKed `AA` on receipt for everything (`322b753`)
— simpler receiver, burst-proof, and **wrong**, because it told the sender "accepted" for messages we later rejected
and left rejections invisible to them; this version keeps the async seam where it earns its keep and puts the
verdict back where the sender can see it. Rejected: `AA`-for-everything (above); honest REST (`400/422` for content
errors — wrong for a retrying sender); `CA` (commit accept) instead of `AA` — only exists in enhanced-ack mode, and
Woodbine's messages are original mode.

**2. Every payload is stored raw with its verdict, in one write, before the ACK; the `messages` table is the work
queue, the audit trail, the quarantine, and the idempotency ledger. Idempotency by (facility, app, control ID).**
One table, one row per request, one `status` column: `queued` is the work queue (durable across restarts — the
worker sweeps it on startup), `rejected`/`failed` is the quarantine (bytes kept, replayable after a parser fix or a
conversation with Woodbine), `accepted`/`duplicate` is the ledger. No broker to run, no second store to keep
consistent, and the demo can *see* the queue with a `SELECT`. The duplicate check and the INSERT share one
`BEGIN IMMEDIATE` transaction, so two concurrent retries can't both be queued; a partial unique index over live
(`queued`/`accepted`) rows is the backstop; the queued→terminal `UPDATE` is guarded by `status = 'queued'` so the
transition happens exactly once. A single FIFO worker keeps per-sender ordering (a correction must not overtake its
original) and SQLite is single-writer anyway. Duplicates are keyed by sender *and* control ID because two providers
can both send `MSG00001`; only live messages participate, so a corrected re-send of a previously rejected message is
accepted. Rejected: an external queue (Redis/RabbitMQ) — real leverage only past SQLite's ceiling, which 500/day
isn't near; control-ID-only keys; overwriting on duplicate (04's differing body is exactly the case where
overwriting is dangerous, so we keep the original and flag the mismatch); storing before validating (two writes per
message for no gain — nothing is lost either way, because no ACK leaves before the row commits, so a crash mid-request
just means the sender retries). Known limit: a burst from provider A delays *report-writing* (not receipt or the
ACK) for provider B behind it; per-facility worker lanes are the next step if that ever matters.

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
| Woodbine's engine *reads* the ACK (`MSA-1`/`ERR`) and someone monitors `AE`/`AR` on their side — otherwise our rejections are only visible here | Maya couldn't say | Add rejection alerting on our side and/or a notification path to their ops |
| Their engine treats `200 + AE/AR` as "delivered, don't retry" and error-queues it | Maya couldn't say | If it retries on non-AA, re-sends of rejected messages are harmless here (rejected rows aren't deduplicated) but noisy |
| Original-mode acknowledgement (MSH-15/16 empty, as in the samples) | samples | If they run enhanced mode, add `CA`/`CE`/`CR` commit acks |
| Encoding UTF-8 unless MSH-18 says otherwise | `PayloadDecoder` | Add the charset to the switch |
| Timestamps are Eastern (Ontario) but sent without offset | stored as-is | Apply the offset at read time / add a per-provider TZ |
| Only `ORU^R01`; ADT is out of scope | `ValidationPolicy.Default` | Add the type to `AcceptedMessageTypes` and an extractor |
| Corrections/addenda arrive as new messages (new control ID); every version is kept | data model | Add a "current report per accession" view or an amendment status |
| Sender identity is trusted from MSH-3/4 (no auth on the endpoint) | — | Per-provider API keys bound to MSH-4 (first item under "more time") before any real PHI flows |

## What I'd Do With More Time

Roughly in the order I'd do them for a real go-live.

**Trust and security (this is PHI over HTTP)**

- **API keys per provider — required on every request.** We generate a key per provider (`providers` table:
  id, MSH-4 facility, hashed key, created/rotated/revoked-at), the client sends it in `Authorization: Bearer …`, and
  a small auth middleware validates it (constant-time hash compare) and rejects missing/revoked keys with `401`
  *before* the body is read. Two things fall out for free: the key **binds the request to a provider identity**, so a
  message whose MSH-4 doesn't match the key's facility is rejected as spoofed instead of trusted (closing the
  "sender identity is trusted from MSH-3/4" assumption above), and rotation/revocation is an `UPDATE`, no redeploy.
  Plus the rest of the usual list: TLS termination, IP allowlist, request size limits, non-root container user,
  secrets management, audit of who read what.
- **Signed responses.** Add `X-Signature: ed25519=<base64>` and `X-Signature-Timestamp` over
  `timestamp + "." + response body`, signed with our private key; publish the public key at a well-known endpoint so
  a provider can verify the ACK (and any GET) came from us and wasn't altered in transit or replayed (timestamp
  window + the ACK's own MSA-2/MSH-10 as a nonce). Asymmetric rather than HMAC so the verifier holds nothing that
  can *forge* a signature. Symmetric HMAC with the provider's key would be the smaller first step.

**Provider integration**

- **Webhooks for processing updates.** Providers subscribe (`callback URL`, `secret`, event filter); we emit
  `message.accepted` (reports written), `message.failed`, and later lifecycle events (amended, superseded) — the
  synchronous ACK already covers `rejected`/`duplicate`, so the webhook is what tells them the *async* half
  finished. Implementation is an outbox: the worker's status transitions insert into an `events` table in the same
  transaction; a second background loop delivers them (POST, HMAC-signed with the subscription secret,
  `Idempotency-Key = message id + event`, exponential backoff, dead-letter after N attempts, delivery log visible
  at `GET /messages/{id}`). Same durability story as the message queue: nothing lives only in memory.
- **Per-provider validation extensions.** HL7 "standard" isn't. `ValidationPolicy` today is a fixed set of
  knobs; make it a list of rules — `IValidationRule { string Name; Rejection? Check(OruMessage, Message raw) }` —
  with the current checks as the built-in set and per-provider additions registered on the `ProviderProfile`
  (e.g. "Woodbine: OBR-32 principal result interpreter required", "provider X: OBX-2 must be TX or FT",
  "provider Y: PID-3 must carry an OHIP repetition"). Rules run in order, first failure wins, each rule names itself
  in the rejection so the sender sees *which* provider-specific rule bit. Simple predicates in code first; a small
  declarative form (segment/field/regex/required) in configuration once there are three providers' worth of them.
- **Provider profiles from configuration/DB** instead of code (fields, policy, rules, API key, webhook
  subscriptions, encoding, timezone), with a small admin surface. Contract test suites per provider from real
  de-identified samples.
- **MLLP listener.** Most hospital engines speak MLLP/TCP, not HTTP. `MessageReceiver.Receive(bytes)` is
  transport-agnostic and returns the ACK text; the library already has MLLP frame extraction; a TCP listener that
  frames with `0x0B … 0x1C 0x0D` and writes the ACK back is a contained addition.
- **Per-provider processing lanes.** One worker per sending facility (ordering preserved within a provider), so a
  burst from one provider can't delay report-writing for another. Receipt and the ACK are already isolated; this is
  about the time between `AA` and `accepted`.

**Operations**

- Metrics (queued/accepted/rejected/duplicate/failed per provider, queue depth, processing latency), alerting on
  rejection spikes and on `pending` growing, a replay path for quarantined/failed messages
  (`POST /messages/{id}/replay` = re-validate and set status back to `queued`), structured JSON logs with correlation
  IDs, OpenTelemetry.
- **Graceful drain on shutdown / a proper poison-message policy** — today an in-flight message is left `queued`
  and picked up on restart; a message that throws is marked `failed` after one attempt (no retries, no backoff).
- **Schema migrations** (DbUp/EF migrations) once the schema starts changing under real data. Retention/purge
  policy for raw payloads (PHI).

**Data and reads**

- **Amendments and report lifecycle.** Preliminary → final → corrected (OBX-11 `P`/`F`/`C`, OBR-25). Today every
  message yields new report rows; add a "latest per (facility, accession)" view or an explicit supersedes link.
- **Report-centric reads.** Today's API is message-centric (what was sent, what happened). Add
  `GET /reports?accession=…&patientId=…&facility=…` for the PocketHealth-side question ("this patient's reports"),
  with paging.
- **Embedded documents.** Radiology reports often arrive as base64 PDFs in an `ED` OBX; store them as blobs/files.

## Notes for the Reviewer

- Layout: `src/Hl7Receiver/{Hl7,Ingestion,Storage,Http}`; `Program.cs` is just wiring. Read it in this order:
  `Http/MessagesEndpoint` → `Ingestion/MessageReceiver` (sync: validate → store → ACK) →
  `Ingestion/MessageEvaluator` (the pure pipeline both halves share) → `Ingestion/ProcessingWorker` →
  `Ingestion/MessageProcessor` (async: re-extract → write reports) → `Hl7/*` (parser wrapper, extractor,
  validator, provider profile, ACK builder) → `Storage/*`. Tests are in `tests/Hl7Receiver.Tests` — the eight
  samples are copied in as fixtures and drive [`SampleBehaviorTests`](tests/Hl7Receiver.Tests/SampleBehaviorTests.cs);
  `EndpointTests` covers the leniency and the provider seam; `AsyncProcessingTests` covers the sync/async seam
  (rejections final at receipt, queued→accepted, duplicate-of-queued, restart recovery, bursts and FIFO);
  `ReadEndpointTests` covers the GET API; `UnitTests` covers the ACK builder, MSH sniffing, timestamps.
- Tests run the real background worker; helpers poll `messages.status` until `queued` resolves (milliseconds).
- Built and tested entirely inside Docker (no local SDK); what you run is what I ran.
- AI use (Claude Code) is described in [PLAN.md](PLAN.md), including what was deliberately not delegated.
- The container runs as root so the `./data` bind mount works on any host without permission games; a hardened
  image would use the `app` user and a named volume.
