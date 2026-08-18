# HL7 ORU Ingestion Server

Your task is described in the take-home brief. This README is yours to fill in.

## Run

```bash
docker compose up --build
```

The server should listen on `http://localhost:8080`.

## Test

```bash
curl -X POST http://localhost:8080/messages \
  -H "Content-Type: text/plain" \
  --data-binary @samples/02_oru_valid_01.hl7
```

Try each of the sample files in `samples/` and confirm the behavior matches what you'd want.

## Decisions and Tradeoffs

<!--
The 2–3 most important choices you made.
For each: what you picked, what you rejected, and why.
-->

## What I'd Do With More Time

<!-- Be specific. Bullets are fine. -->

## Notes for the Reviewer

<!-- Anything you want us to know before reading the code. Optional. -->
