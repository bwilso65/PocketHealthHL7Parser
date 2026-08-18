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

### 2026-08-18 — Session 1: read, clarify, scaffold

- Read the brief and scaffold. Scaffold README fixes the ingress contract: `POST /messages`, `Content-Type: text/plain`,
  port 8080, env `DB_PATH` / `PORT`, `./data` bind mount. That means **HL7 over HTTP**, not MLLP — flagged as the #1
  question for Maya, since Maya said "over HL7" and hospitals overwhelmingly send MLLP/TCP.
- Inspected samples at the byte level: segments are `\r`-terminated; some files end `\r\n`, one (`07`) ends in a bare
  `\n`; `04` contains a UTF-8 em-dash; `04`'s "retry" has *different* content than the original `02` (no address,
  different OBX text). The samples README says there is no single correct behavior — this is a strictness/leniency test.
- Wrote the ambiguity list (transport, ACK expectations, encoding, batching, message-type scope, required-field policy,
  duplicate semantics, sender identity, data model, timezone, sync/async, HTTP status semantics, escapes, auth/PHI).
  Split it into (a) decisions only I can make, (b) questions for Maya, (c) engineering defaults I'll take and defend.
- Drafted a per-sample behavior matrix (below, "Decision log" D5) as the working hypothesis, pending Maya's answers.
- Environment check: Docker Desktop present; **no Go / .NET SDK / sqlite3 CLI locally**. Decision: build and iterate
  entirely inside Docker (the image is the deliverable anyway).
- Researched C# HL7 parsers (see D3). Downloaded and read the chosen library's parser/validation source to know exactly
  what it enforces and what it leaves to us.
- Scaffolded the solution with the .NET 10 SDK container, wrote Dockerfile (multi-stage: build → test → publish →
  runtime with `sqlite3` CLI), compose (`hl7-server` + a `tests` profile), config plumbing, SQLite bootstrap (WAL),
  `/healthz`, and a smoke test. Verified: `docker compose --profile test run --rm tests` passes;
  `docker compose up --build` is healthy; DB appears in `./data`; `docker compose exec hl7-server sqlite3 ...` works.
- Drafted the questions for Maya's assistant (transport, interface spec, report format, amendments, identifiers,
  ACK/retry, error handling, volume, encoding/TZ, security, go-live process, the other 3 providers). Domain logic is
  paused until the design-changing answers are back.

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
  `\r`/`\n`/`\r\n`, reads delimiters from MSH-2, decodes escape sequences, gives path access (`PID.5.1`),
  repetitions, ACK/NACK generation, HL7 timestamp parsing, and MLLP frame extraction. Its baseline validation is
  small and known (read the source): starts with `MSH`, MSH has ≥ 11 field separators, MSH-9/10/11 non-empty, segment
  names match `[A-Z][A-Z][A-Z1-9]`, 4th char of every segment equals the field delimiter. It does **not** enforce
  message structure, reject a second MSH, require OBX-11, or check message type — so **the strict-vs-lenient policy is
  ours to write and defend**, which is exactly what this exercise is about. Rejected: nHapi 3.2.4 (MPL-2.0; full HAPI
  port with generated per-version models — schema-aware and strict by default, which fights the "every provider has
  quirks" reality and adds a lot of surface area for a receiver that mostly needs ~15 fields); hand-rolled (fine for
  these 8 files, but escapes, repetitions, sub-components, and Z-segments make it grow fast). Would change if: we needed
  strict conformance profiles per provider (then nHapi's validation model earns its weight).
- **D4 — Storage: `Microsoft.Data.Sqlite` + Dapper, hand-written SQL, `CREATE ... IF NOT EXISTS` on startup.** Small,
  transparent, and easy to extend live. WAL mode so the DB can be inspected while the server writes. Rejected: EF Core
  (migrations + model ceremony for ~3 tables); a bare `SqliteCommand` everywhere (Dapper removes boilerplate without
  hiding SQL). Would change if: the schema starts evolving in production — then a real migration tool.
- **D5 — Behavior matrix for the samples (working hypothesis; revisit after Maya).** Underlying principle: **every
  payload is stored raw with a status** (`accepted` / `duplicate` / `rejected` + error), so rejecting is never data
  loss — it's quarantine with replay. Idempotency key = (sending facility, sending app, control ID), not control ID
  alone (two providers can both send `MSG00001`).
  | # | Sample | Behavior | Why |
  |---|---|---|---|
  | 01–03 | valid | accept | 03 proves sender is a first-class column, not a hard-coded "WOODBINE" |
  | 04 | duplicate control ID, different body | idempotent success; store receipt as `duplicate`; flag body-hash mismatch | sender retries until success — a failure response creates a retry loop; never silently overwrite a stored report with different content |
  | 05 | broken MSH | reject (unparseable) | can't identify the message |
  | 06 | two messages in one payload | reject whole payload | one POST = one message; can't return one honest ACK for two control IDs; splitting hides a sender bug; taking only the first silently loses a report. Raw kept. |
  | 07 | truncated in OBX-5 | reject (invalid) | OBX-11 (result status, HL7-required) missing → treat as incomplete. A partial radiology report stored as complete is a patient-safety issue |
  | 08 | ADT^A01 | reject (unsupported type) | not a report |
  Open until Maya answers: exact response body format; whether 06 should be split; whether ADT is wanted later.

## AI usage

- Tool: **Claude Code (Claude Fable 5)**, in this repo, as a pair programmer. This whole file is written with it open.
- What it did so far: read the samples byte-by-byte and produced the line-ending/encoding census; drafted the ambiguity
  list and the Maya questions (I edited and chose which to ask); researched the parser libraries and pulled the
  chosen library's source so we could read its validation logic instead of guessing; scaffolded the solution,
  Dockerfile, compose, DB bootstrap, and smoke tests; keeps this log.
- What I did: made every decision in the decision log; chose the language for the live session; chose to build only
  inside Docker; pushed for a library over a hand-rolled parser; own the conversation with Maya's assistant.
- What I deliberately did *not* delegate: the strict-vs-lenient policy per sample. The AI proposed a matrix; I'm
  treating each row as a call I have to be able to defend, and several are waiting on Maya's answers.

## Dead ends / backed out

- Considered `HL7-dotnetcore` (2.1M downloads) first — it's been deprecated (July 2024) in favor of `HL7-V2` by the
  same maintainer; switched before writing any code.
- Started with `UseUrls` to honor `PORT`; the aspnet base image already sets `ASPNETCORE_HTTP_PORTS`, so that logged
  an override warning on every start. Switched to setting `HTTP_PORTS` from `PORT` instead.
