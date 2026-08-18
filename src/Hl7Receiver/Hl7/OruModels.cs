namespace Hl7Receiver.Hl7;

/// <summary>What we extract from an ORU^R01. Values are as sent (escape sequences decoded); nothing is normalized yet.</summary>
public sealed record OruMessage(MessageHeader Header, PatientInfo Patient, IReadOnlyList<ReportInfo> Reports);

/// <summary>Patient demographics as carried in this message's PID. A snapshot, not a master record.</summary>
public sealed record PatientInfo(
    string? Identifier,                    // PID-3.1 (first repetition)
    string? IdentifierAssigningAuthority,  // PID-3.4
    string? IdentifierTypeCode,            // PID-3.5, e.g. MR
    string? FamilyName,                    // PID-5.1
    string? GivenName,                     // PID-5.2
    string? MiddleName,                    // PID-5.3
    string? DateOfBirth,                   // PID-7 (HL7 TS as sent)
    string? Sex);                          // PID-8

/// <summary>One OBR and the OBX segments that follow it. A message may carry several.</summary>
public sealed record ReportInfo(
    int Sequence,                          // 1-based position of the OBR within the message
    string? PlacerOrderNumber,             // OBR-2.1
    string? AccessionNumber,               // per provider profile; default OBR-3.1 (filler order number)
    string? ProcedureCode,                 // OBR-4.1
    string? ProcedureDescription,          // OBR-4.2
    string? ProcedureCodingSystem,         // OBR-4.3
    string? ObservationDateTime,           // OBR-7 (HL7 TS as sent)
    string? ResultStatus,                  // OBR-25 (often absent)
    IReadOnlyList<ObservationInfo> Observations)
{
    /// <summary>The narrative: OBX-5 values in order, one per line. What a clinician would read.</summary>
    public string ReportText => string.Join("\n", Observations.Select(o => o.Value ?? string.Empty));
}

public sealed record ObservationInfo(
    int? SetId,                            // OBX-1
    string? ValueType,                     // OBX-2, e.g. TX / FT / ST / ED
    string? Identifier,                    // OBX-3.1
    string? IdentifierText,                // OBX-3.2
    string? Value,                         // OBX-5 (whole field; HL7 line breaks normalized to '\n')
    string? Units,                         // OBX-6.1
    string? ResultStatus);                 // OBX-11, e.g. F (final), P (preliminary), C (corrected)
