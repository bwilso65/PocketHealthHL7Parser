# Sample HL7 Messages

These files use the standard HL7 v2 segment terminator: **`\r` (carriage return)**, not `\n`. Many text editors will display them as one long line — that's expected.

To view them with segments on separate lines:

```bash
tr '\r' '\n' < 02_oru_valid_01.hl7
```

Files are numbered so they sort in the recommended order to work through them: valid messages first, then edge cases and malformed inputs.

## Files

### Valid ORU messages

| File | Description |
|------|-------------|
| `01_oru_valid_minimal.hl7` | Just enough required fields — strict-vs-lenient tester |
| `02_oru_valid_01.hl7` | Full ORU^R01 with multi-OBX report |
| `03_oru_valid_02.hl7` | Different sender, single OBX |

### Edge cases

| File | Description |
|------|-------------|
| `04_oru_duplicate_retry.hl7` | Same Message Control ID as `02_oru_valid_01.hl7` — simulates a sender retry |
| `05_malformed.hl7` | Broken structure, truncated MSH |
| `06_malformed_double_msh.hl7` | Two messages concatenated into one payload (a real-world sender bug) |
| `07_malformed_truncated.hl7` | Looks valid up to the last OBX, then cut off mid-field |
| `08_adt_wrong_type.hl7` | Well-formed HL7 but wrong message type (ADT, not ORU) |

Test each one against your server. Decide what behavior makes sense for each, document your reasoning in the tradeoffs section, and be ready to defend it in the walkthrough.

There is no single "correct" behavior for every edge case here — that's the point. We want to see how you reason about strictness vs. leniency in a real-world ingestion path.
