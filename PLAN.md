# Plan / Log

Rough, chronological, and honest. Decisions are logged as they're made (see "Decision log"), and the AI usage
section says what was delegated and what wasn't. This file is one of the deliverables, so it is written to be read.

## Approach

1. **Read everything before writing code.** The brief, the scaffold, and all 8 sample messages (byte-level — line
   endings and encoding are part of the test). Enumerate every ambiguity and judgment call up front.
2. **Ask Maya (her assistant) the design-changing questions first** — transport (MLLP vs HTTPS), report format
   (OBX text vs embedded PDF), amendments/corrections, identifiers, ACK/retry expectations — and the
   assumption-validating ones second. The log is reviewed, so ask like an engineer who owns the integration.
3. **Pick the stack, scaffold, and prove the Docker loop** (build → test → run) before any domain logic.
4. **Build the pipeline** `receive → parse → validate (policy) → persist → respond`, driven by the 8 samples as
   table-driven tests. Store every payload raw first; extraction/validation are layered on top and can be re-run.
5. **Docs**: README (top tradeoffs, "with more time"), this file, and a demo script.

## Log

### 2026-08-18 — Session 1: read, clarify, scaffold, build

- Read the brief and scaffold. Scaffold README fixes the ingress contract: `POST /messages`, `Content-Type: text/plain`,
  port 8080, env `DB_PATH` / `PORT`, `./data` bind mount. That means **HL7 over HTTP**, not MLLP — flagged as the #1
  question for Maya, since Maya said "over HL7" and hospitals overwhelmingly send MLLP/TCP.
- Inspected samples at the byte level: segments are `\r`-terminated; some files end `\r\n`, one (`07`) ends in a bare
  `\n`; `04` contains a UTF-8 em-dash; `04`'s "retry" has *different* content than the original `02` (no address,
  different OBX text). The samples README says there is no single correct behavior — this is a strictness/leniency test.
- Wrote the ambiguity list (transport, ACK expectations, encoding, batching, message-type scope, required-field policy,
  duplicate semantics, sender identity, data model, timezone, sync/async, HTTP status semantics, escapes, auth/PHI).
  Split it into (a) decisions only I can make, (b) questions for Maya, (c) engineering defaults I'll take and defend.
- Environment check: Docker Desktop present; **no Go / .NET SDK / sqlite3 CLI locally**. Decision: build and iterate
  entirely inside Docker (the image is the deliverable anyway).
- Researched C# HL7 parsers (see D3). Downloaded and read the chosen library's parser/validation/serialization source
  to know exactly what it enforces, what it leaves to us, and that escape decoding round-trips.
- Scaffolded the solution with the .NET 10 SDK container, wrote Dockerfile (multi-stage: build → test → publish →
  runtime with `sqlite3` CLI), compose (`hl7-server` + a `tests` profile), config plumbing, SQLite bootstrap (WAL),
  `/healthz`, and a smoke test. Verified: `docker compose --profile test run --rm tests` passes;
  `docker compose up --build` is healthy; DB appears in `./data`; `docker compose exec hl7-server sqlite3 ...` works.
- **Talked to Maya's assistant.** What came back (full log is on the take-home page):
  - Woodbine sends **HTTP POST, synchronously**; **their sender retries on non-2xx** → answer 2xx once we've
    accepted the message, even if processing isn't finished. Sync vs async processing is my call.
  - Volume ~50/day → ~500/day in 6 months, can burst. Timezone almost certainly Eastern. Encoding unknown.
  - "Viewed in the database" = backend only; a SQL query showing patient + report is enough. No UI.
  - Provider flexibility for the next three: "leadership wants this to be *the* standard pattern"; rigid vs
    hooks-for-quirks is my architecture call — document it here.
  - She could not answer (would need Daniel at Woodbine): prelim/final/addenda workflow, which fields carry accession
    and patient ID, whether they expect a standard HL7 ACK and their retry policy, error queue / who to notify, ADT
    plans, real-time vs batched, latency expectations. Her steer: **pick sensible defaults, document them, list what
    to confirm before go-live.** Done — README "Assumptions to confirm with Woodbine before go-live".
- Built the pipeline (`Hl7/`, `Ingestion/`, `Storage/`, `Http/`) and the tests: 53 tests, all green in Docker on
  the first full run. Ran the real demo: `docker compose up --build`, `scripts/send-samples.sh`, `scripts/show-db.sh`
  — the ACKs and the DB match the behaviour matrix. Wrote README.
- **Maya round 2** — asked what the stored output should look like (plain-English vs raw fields; patient records vs
  relational) and what "queryable" means in practice. Answers: both are engineering calls, "document *why* in the
  PLAN" (→ D10); for verification, "a CLI query, an HTTP endpoint returning JSON, or a manual SQL query — pick what's
  natural, document it; the key is a clear, reproducible way to verify the data is in there and correct."
  Decision: since ingest is already HTTP, add a read API keyed by message (→ D11), keep the SQL path too.
- **Maya round 3** — ran the response contract past her (HTTP status = "did we accept the bytes", ACK = verdict).
  She endorsed it for the take-home and named what she *doesn't* know: whether Woodbine's engine expects/reads a
  standard HL7 ACK, whether they monitor AE/AR or just retry on non-2xx, whether they have ops monitoring, how they
  handle concatenated payloads and duplicates on their side. All added to the README "assumptions to confirm" table
  as questions for Daniel; she also flagged "is this the right pattern for all 4 providers" as a live-session topic.
- Added `GET /messages/{id}`, `GET /messages/{id}/raw`, `GET /messages?controlId=&facility=&status=&limit=`;
  8 more tests (61 total, green); re-ran the container demo and exercised the GETs by hand.
- **Review decision: split receipt from processing (async).** After reviewing the sync version I confirmed the HTTP
  contract (200 = we reliably have the data, regardless of the file's condition; 400 only for an empty body; 5xx if
  we can't store) and decided processing should be asynchronous: the receiver's job is only "we have the file or we
  don't", and a burst from one provider (Maya: their queue can back up) must not slow down receipt for others.
  Before building it, the trade-offs were written down (D6/D7): the sender no longer sees `AE`/`AR`; a single FIFO
  worker isolates *receipt* but not *processing latency* between providers; `AA` vs `CA` for the receipt ACK.
- Implemented: `MessageReceiver` (INSERT + wake signal), `ProcessingQueue` (in-process wake-up; the durable queue is
  the `messages` table), `ProcessingWorker` (`BackgroundService`: startup sweep, signal-driven drain, 30 s safety
  sweep, `failed` on exception), `MessageProcessor` (the pipeline, unchanged logic), `messages.processed_at`, statuses
  `received`/`failed`, `/healthz` with queue depth, verdict writes guarded by `status='received'`. Removed the NACK
  builder and the AE/AR classification (dead under async; in git history at `d2a41b1`). Tests updated to await the
  verdict; 6 new async tests (receipt-before-verdict, restart recovery from `received`, 40-message burst + FIFO,
  direct drain, queue depth). 67 green, run twice for flakiness. Demo scripts now show the ACK *and* the verdict.
- **Reversed the always-`AA` part.** On review: answering `AA` to a message we then reject is a lie to the sender,
  and it makes our rejections invisible to their engine. The AI had flagged that as "the trade-off" and built it
  anyway; it should have pushed back with the alternative up front. The alternative is the obvious hybrid: keep the
  quick validation *in the request* so the ACK is honest (`AA`/`AE`/`AR` + `ERR`), keep the raw bytes landing in
  `messages` immediately, and move only the report-writing (and future heavier work) to the async worker.
- Implemented the hybrid: `MessageEvaluator` (the pure pipeline, shared), `MessageReceiver` = evaluate → dup check
  + single INSERT in one transaction → ACK; statuses `queued → accepted | failed`, `rejected`/`duplicate` at receipt;
  `ack_code` column; NACK builder restored; validation exceptions at receipt → row `failed` + `AE` 207. Tests
  reworked (69 green, clean build); container demo re-run — ACKs and DB match the matrix.
- Expanded README "What I'd do with more time" with the items I'd actually schedule first for a go-live: per-provider
  API keys (generate/validate/revoke, bound to MSH-4 so a key can't send as another facility), signed responses so
  the client can verify the ACK came from us, webhooks for async processing updates (outbox off the worker's status
  transitions), and per-provider validation extensions (rule list on the profile — HL7 "standard" is not very
  standardized). Grouped the list: trust/security → provider integration → operations → data.

## Decision log

Format: what / why / what was rejected / what would change it.

- **D1 — Language: C# on .NET 10 LTS.** Preferred stack per the brief (Go or C#) and the one I can extend live under
  observation without stalling. .NET 10 because 8 leaves support Nov 2026 and 9 already has. Rejected: Go (scaffold's
  Dockerfile was Go, but fluency for the live session wins).
- **D2 — Iterate entirely inside Docker.** No local SDK; the compose stack is the deliverable, so what I test is what
  ships. Tradeoff: slower inner loop. Mitigation: multi-stage Dockerfile with a cached restore layer and a `tests`
  compose profile. (For the live session I'd install the SDK locally for a fast loop; behavior is identical.)
- **D3 — HL7 parsing: `HL7-V2` (Efferent, NuGet `HL7-V2` 3.8.0, MIT, zero dependencies), not nHapi, not hand-rolled.**
  It is deliberately schema-free ("not tied to any HL7 version nor validates against one"): it splits segments on
  `\r`/`\n`/`\r\n`, reads delimiters from MSH-2, decodes escape sequences (and re-encodes them symmetrically, so parsing
  round-trips), gives path access (`PID.5.1`), repetitions, HL7 timestamp parsing, and MLLP frame extraction. Its
  baseline validation is small and known (read the source): starts with `MSH`, MSH has ≥ 11 field separators,
  MSH-9/10/11 non-empty, segment names match `[A-Z][A-Z][A-Z1-9]`, 4th char of every segment equals the field
  delimiter. It does **not** enforce message structure, reject a second MSH, require OBX-11, or check message type —
  so **the strict-vs-lenient policy is ours to write and defend**, which is exactly what this exercise is about.
  Rejected: nHapi 3.2.4 (MPL-2.0; full HAPI port with generated per-version models — schema-aware and strict by
  default, which fights the "every provider has quirks" reality and adds a lot of surface area for a receiver that
  mostly needs ~15 fields); hand-rolled (fine for these 8 files, but escapes, repetitions, sub-components, and
  Z-segments make it grow fast). Would change if: we needed strict conformance profiles per provider (then nHapi's
  validation model earns its weight). One quirk found by reading the source: it decodes `\.br\` to a literal `<BR>`;
  we map that to `\n` in the extractor. We build the ACK by hand (20 lines) rather than with the library so the same
  code path works for unparseable input.
- **D4 — Storage: `Microsoft.Data.Sqlite` + Dapper, hand-written SQL, `CREATE ... IF NOT EXISTS` on startup.** Small,
  transparent, and easy to extend live. WAL mode so the DB can be inspected while the server writes. Rejected: EF Core
  (migrations + model ceremony for ~3 tables); a bare `SqliteCommand` everywhere (Dapper removes boilerplate without
  hiding SQL). Would change if: the schema starts evolving in production — then a real migration tool.
- **D5 — Behaviour matrix for the samples.** Underlying principle: **every payload is stored raw with a status**
  (`accepted` / `duplicate` / `rejected` + reason), so rejecting is never data loss — it's quarantine with replay.
  Idempotency key = (sending facility, sending app, control ID), not control ID alone (two providers can both send
  `MSG00001`); only *accepted* rows participate, so a corrected re-send of a rejected message can be accepted.
  | # | Sample | Behaviour | Why |
  |---|---|---|---|
  | 01–03 | valid | accept | 03 proves sender is a first-class column, not a hard-coded "WOODBINE" |
  | 04 | duplicate control ID, different body | idempotent success; store receipt as `duplicate`; flag body-hash mismatch | sender retries until success — a failure response creates a retry loop; never silently overwrite a stored report with different content |
  | 05 | broken MSH | reject `UNPARSEABLE` (AR) | can't identify the message; sender still sniffed from the partial MSH |
  | 06 | two messages in one payload | reject whole payload `MULTIPLE_MSH` (AR) | one POST = one message; can't return one honest ACK for two control IDs; splitting hides a sender bug; taking only the first silently loses a report. Raw kept. |
  | 07 | truncated in OBX-5 | reject `REQUIRED_FIELD_MISSING` OBX-11 (AE) | OBX-11 (result status, HL7-required) missing → treat as incomplete. A partial radiology report stored as complete is a patient-safety issue |
  | 08 | ADT^A01 | reject `UNSUPPORTED_MESSAGE_TYPE` (AR) | not a report; HL7 table 0357 code 200 |
  AE vs AR follows HL7's intent: AR = "can't/won't process this kind of thing" (syntax, type, structure), AE = "understood it, content isn't acceptable" (missing required field). Both come back with an `ERR` segment carrying a table-0357 code.
- **D6 — HTTP status = "do we reliably have your bytes"; the ACK = the verdict. `200` for anything durably stored,
  regardless of content.** After Maya's "their sender will retry on non-2xx": a permanently-bad message answered with
  `4xx` would be retried forever, so `200` = stored, `400` = nothing to store (empty body), `5xx` = couldn't persist
  (the one case where a retry is right); the application verdict travels in `MSA-1` + `ERR`, the same split HL7
  itself makes between commit-level and application-level acknowledgement. Confirmed on review. Rejected: `400/422`
  for rejections (better REST hygiene, wrong for a retrying sender).
- **D7 — Validate synchronously; write reports asynchronously.** This went through three versions, and the arc is
  the point. (v1, `d2a41b1`) everything synchronous: honest ACK, simple, but receipt coupled to processing.
  (v2, `322b753`) on review I asked for async processing — cleaner receiver ("we have the file or we don't"), bursts
  absorbed by a queue, one provider's burst can't slow another's receipt — and it was built as *store raw + `AA` for
  everything, verdict later*. The AI flagged the consequence ("the sender never learns about a rejection through the
  ACK") as a documented trade-off and built it. That was the wrong call, and on review I said so: an `AA` we later
  contradict is a lie, and rejections become invisible to the sender's engine. (v3, this) the hybrid I should have
  been offered up front: the *quick validation* (parse → envelope → extract → required fields → duplicate check;
  pure in-memory, ~1 ms) runs in the request so the ACK is honest — `AA` queued / duplicate, `AE`/`AR` + `ERR`
  rejected — the raw bytes land in `messages` in one write *with* the verdict before the ACK leaves, and only the
  report-writing (and future heavier work: patient matching, embedded PDFs, notifications) happens in the worker.
  Receipt is still isolated per provider and still cheap; the queue still absorbs bursts of DB writes; and the
  sender is told the truth. Design details: `MessageEvaluator` is the shared pure pipeline (receiver and worker both
  run it — the worker re-extracts from the stored bytes, so nothing has to survive in memory and a restart changes
  nothing); dup check + INSERT share one `BEGIN IMMEDIATE` transaction (concurrent retries can't both be queued;
  partial unique index over live rows as backstop); `queued → accepted|failed` `UPDATE` guarded by `status='queued'`;
  the worker sweeps on startup / on signal / every 30 s; a validation exception at receipt (our bug) stores the row
  `failed` and answers `AE` 207 — a retry wouldn't help; a worker exception marks `failed`, replayable. Rejected:
  `AA`-for-everything (v2 — above); `CA` instead of `AA` (only exists in enhanced-ack mode; Woodbine is original
  mode); an external broker (nothing to gain below SQLite's ceiling); N parallel workers (SQLite is single-writer,
  ordering would need care); per-facility lanes now (YAGNI with one provider — documented next step; note the
  remaining limit is only the time between `AA` and `accepted`, not receipt or the ACK); storing before validating
  (two writes for nothing — no ACK leaves before the row commits, so a crash mid-request just means the sender
  retries).
- **D8 — Provider flexibility: a `ProviderProfile` (validation policy + field mapping) resolved by MSH-4, default for
  everyone today.** Answering Maya's "rigid pipeline vs hooks for quirks": the pipeline is fixed
  (decode → parse → validate → extract → persist → ACK) and the *knobs* are data — which message types, which fields
  are required, where the accession number and patient ID live. That is where provider quirks actually show up. No
  config file yet (nothing to configure: Woodbine fits the default, and the other three are unknown), but the seam is
  real and tested (`Provider_profile_override_changes_field_mapping_for_that_sender_only`). Rejected: per-provider
  code paths (diverge fast, untestable) and a fully generic mapping DSL (YAGNI with one provider).
- **D9 — Charset handling: MSH-18 if declared and known, else strict UTF-8, else ISO-8859-1 fallback.** Maya
  doesn't know Woodbine's encoding; the samples are UTF-8. Lenient UTF-8 decoding would silently turn a Windows-1252
  `é` into U+FFFD *and store it* — the fallback keeps accented names readable, and the raw bytes are stored regardless
  so a wrong guess is recoverable. Logged as a warning when the fallback fires.
- **D10 — Data model.** `messages` (raw + outcome) → `reports` (one per OBR: patient snapshot from PID, order/procedure
  from OBR, `report_text` = OBX-5 values newline-joined) → `observations` (one per OBX). Patient demographics are a
  *snapshot on the report*, not a patient master (identity resolution across MRNs/health-card numbers is a
  downstream concern and needs ADT). Timestamps → ISO-8601 with the precision sent and **no invented offset** (Maya:
  "almost certainly Eastern" is documented, not baked in). Column named `accession_number` (meaning) rather than
  `filler_order_number` (source) because the *mapping* is the provider-specific part and the table should be canonical.
  Why normalized tables rather than a JSON blob (Maya: "both are defensible, say why"): the day-one questions are
  relational — "reports for this patient", "everything from this provider", "rejections this week", "is this
  accession already here" — and SQLite answers those with indexes, not JSON path scans; the raw bytes are stored
  alongside so nothing the schema doesn't model is lost; and a fixed schema is what a second consumer (a UI, an
  export) can build against. Rejected: JSON blob per message (fast to write, every query becomes application code);
  a patient master table (needs identity resolution and ADT — the wrong place to invent it).
- **D11 — Read API: `GET /messages/{id}` (+ `/raw`) and `GET /messages?controlId=&facility=&status=&limit=`.**
  Maya: any reproducible verification path is fine (CLI, endpoint, SQL). Since ingest is already HTTP, the natural
  demo is *send → get id back → GET it*, and the natural ops question is "what happened to MSG00042?" — so the API is
  keyed by **our** id (what the POST returns in `X-Message-Id`/`Location`/JSON) *and* searchable by the **sender's**
  control ID (only unique per facility, hence a list). `/raw` returns the exact stored bytes so a quarantined
  message can be inspected without opening the DB. Message-centric on purpose: it answers "what did you send and
  what did we do with it"; a report-centric API (`/reports?patientId=`) is the PocketHealth-side view and is listed
  under "with more time". The `sqlite3` CLI path stays in the README because it's zero-code and shows the schema.

## Live-session prep notes (for me)

Likely "extend it live" asks and where they land: MLLP listener (`MessageReceiver.Receive(bytes)` is
transport-agnostic and returns the ACK text; library has `MessageHelper.ExtractMessages` for framing); a new provider
with a quirk (`ProviderProfileRegistry` override); accept `ADT` (policy + a new extractor); report-centric read
endpoint (`MessageQueries` pattern → `ReportQueries`); auth (middleware); replay endpoint (re-run
`MessageEvaluator`, `UPDATE messages SET status='queued'` + `ProcessingQueue.Signal()`); per-facility worker lanes
(`ProcessingWorker` → one drain loop per MSH-4); amendments (`OBX-11 = C`, `OBR-25`, latest-per-accession view);
retries/backoff for `failed`; enhanced-mode `CA` acks if a provider sets MSH-15.

## AI usage

- Tool: **Claude Code (Claude Fable 5)**, in this repo, as a pair programmer. This whole file is written with it open.
- What it did: read the samples byte-by-byte and produced the line-ending/encoding census; drafted the ambiguity list
  and the Maya questions (I edited and chose which to ask); researched the parser libraries and pulled the chosen
  library's source so we could read its validation/serialization logic instead of guessing; scaffolded the solution,
  Dockerfile, compose; wrote the first draft of every source file, the tests, the demo scripts, and this log and the
  README, iterating in Docker.
- What I did: made every decision in the decision log; chose the language for the live session; chose to build only
  inside Docker; pushed for a library over a hand-rolled parser; ran the conversation with Maya's assistant and fed
  the answers back (which flipped D6); reviewed the code as it landed.
- What I deliberately did *not* delegate: the strict-vs-lenient policy per sample and the HTTP/ACK contract. The AI
  proposed a matrix; each row is a call I have to be able to defend, and D6 in particular changed after talking to Maya.
- Where the AI got it wrong and I caught it: D7 v2. I asked for async processing; it noted that the ACK would stop
  reflecting validity and built it anyway. It should have led with the hybrid (validate in the request, defer the
  writes). I reversed it on review. That's the working relationship I want with these tools: they build fast, I own
  the contract with the provider.
- Dead ends the AI helped avoid: it found `HL7-dotnetcore` was deprecated before we depended on it; it read the
  library's `Encode`/`Decode` to confirm escaped text round-trips before we relied on that; it caught the `<BR>` quirk.

## Prompt log (abridged)

The substantive prompts, in order. Everything else was "run it / fix that / next".

1. Set up a memory file for a multi-session take-home; push back hard on ambiguity; prep for follow-up questions on
   changing requirements and design rationale.
2. Pasted the assignment brief + noted the scaffold and samples. → Ambiguity list, per-sample behaviour hypothesis,
   prioritized questions for Maya, proposed structure.
3. Decisions: C#; iterate entirely in Docker; ignore the time cap; find a lightweight C# HL7 parser with room for
   custom overrides; HTTP responses to Woodbine, then persist. → Library research (HL7-dotnetcore deprecated →
   HL7-V2; nHapi as heavyweight alternative), scaffold, Docker loop proven, first commit.
4. Pasted the Maya's-assistant conversation. → Response contract flipped to 200-for-stored (D6); pipeline, schema,
   tests, demo scripts, README written and verified in Docker.
5. Pasted Maya rounds 2–3 (data-model shape and "queryable" are our call; 200+ACK endorsed with open questions for
   Daniel) and asked for an endpoint to fetch a sent message by a passed-in value. → Read API (D11) keyed by our id
   and searchable by control ID, tests, docs.
6. Reviewed and confirmed the HTTP contract (200 = we reliably have the data; 400 empty; 5xx trouble) and directed
   the change to asynchronous processing — cleaner receiver, burst isolation between providers. → The AI noted two
   consequences (sender loses AE/AR visibility; single worker isolates receipt but not processing latency), then
   built it as "AA for everything, verdict later".
7. Rejected always-`AA` ("why didn't you push back on this one?") and asked for the hybrid: quick validation parse in
   the receiver with proper ACKs for invalid/unparseable/corrupt messages; raw bytes into `messages` fast; only the
   `reports` population async. → The AI owned the miss, then rebuilt: shared `MessageEvaluator`, sync validate +
   store + honest ACK, worker writes reports; D7 rewritten with the full arc.

## Dead ends / backed out

- Considered `HL7-dotnetcore` (2.1M downloads) first — it's been deprecated (July 2024) in favor of `HL7-V2` by the
  same maintainer; switched before writing any code.
- Started with `UseUrls` to honor `PORT`; the aspnet base image already sets `ASPNETCORE_HTTP_PORTS`, so that logged
  an override warning on every start. Switched to setting `HTTP_PORTS` from `PORT` instead.
- First plan had `4xx` for rejected messages (honest REST). Maya's "sender retries on non-2xx" made that a retry
  storm; replaced with `200 + AE/AR` and made rejections loud elsewhere (D6).
- Tried mapping a nullable value tuple from Dapper for the duplicate check; swapped for a tiny mapping class rather
  than find out how Dapper feels about `Nullable<ValueTuple>` at demo time.
- **The always-`AA` async version (`322b753`).** Built for a day, then reversed. Correct instinct (decouple receipt
  from processing), wrong cut (moved the *verdict* out of the request too, so the ACK stopped being true). Kept the
  worker, the queue-in-the-table, the restart sweep and the guarded transitions; put validation and the NACK builder
  back in the request. Lesson recorded in D7 and in "AI usage": when a directive has a consequence like "the sender
  is told AA for a message we'll reject", propose the alternative *before* building, not in the trade-offs section.
- First cut of the worker's wake-up used `WaitToReadAsync` raced against `Task.Delay`; that leaves a dangling reader
  on timeout. Replaced with a linked `CancellationTokenSource` timeout.
