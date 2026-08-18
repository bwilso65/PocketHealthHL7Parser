using Efferent.HL7.V2;

namespace Hl7Receiver.Hl7;

public sealed record ExtractionOutcome(OruMessage? Value, Rejection? Rejection)
{
    public bool Success => Value is not null;
}

/// <summary>
/// Walks a parsed ORU^R01 in segment order and builds <see cref="OruMessage"/>.
/// Structure enforced here (because it can't be expressed as a missing field): exactly one PID, at least one OBR,
/// every OBX belongs to the OBR that precedes it. Segments we don't model (ORC, NTE, PV1, Z-segments, ...) are
/// ignored — they're still in the raw payload if we need them later.
/// </summary>
public sealed class OruExtractor
{
    public ExtractionOutcome Extract(Message message, MessageHeader header, ProviderProfile profile)
    {
        PatientInfo? patient = null;
        var reports = new List<ReportInfo>();
        ReportBuilder? current = null;

        foreach (var segment in message.Segments())
        {
            switch (segment.Name)
            {
                case "PID":
                    if (patient is not null)
                    {
                        return Reject(Rejection.SegmentSequence("Multiple PID segments; one patient per message is required"));
                    }
                    patient = ReadPatient(segment, profile);
                    break;

                case "OBR":
                    if (current is not null)
                    {
                        reports.Add(current.Build());
                    }
                    current = new ReportBuilder(reports.Count + 1, segment, profile);
                    break;

                case "OBX":
                    if (current is null)
                    {
                        return Reject(Rejection.SegmentSequence("OBX segment appears before any OBR"));
                    }
                    current.Add(ReadObservation(segment));
                    break;
            }
        }

        if (current is not null)
        {
            reports.Add(current.Build());
        }

        if (patient is null)
        {
            return Reject(Rejection.RequiredSegmentMissing("PID"));
        }

        if (reports.Count == 0)
        {
            return Reject(Rejection.RequiredSegmentMissing("OBR"));
        }

        return new ExtractionOutcome(new OruMessage(header, patient, reports), null);
    }

    private static ExtractionOutcome Reject(Rejection rejection) => new(null, rejection);

    private static PatientInfo ReadPatient(Segment pid, ProviderProfile profile) => new(
        Identifier: pid.Get(profile.PatientIdentifier),
        IdentifierAssigningAuthority: pid.Get(3, 4),
        IdentifierTypeCode: pid.Get(3, 5),
        FamilyName: pid.Get(5, 1),
        GivenName: pid.Get(5, 2),
        MiddleName: pid.Get(5, 3),
        DateOfBirth: pid.Get(7),
        Sex: pid.Get(8));

    private static ObservationInfo ReadObservation(Segment obx) => new(
        SetId: int.TryParse(obx.Get(1), out var setId) ? setId : null,
        ValueType: obx.Get(2),
        Identifier: obx.Get(3, 1),
        IdentifierText: obx.Get(3, 2),
        Value: NormalizeText(obx.Get(5)),
        Units: obx.Get(6, 1),
        ResultStatus: obx.Get(11));

    /// <summary>
    /// The library decodes the HL7 formatted-text line break (<c>\.br\</c>) to a literal "&lt;BR&gt;".
    /// Report text should read as text, so map it to a newline.
    /// </summary>
    private static string? NormalizeText(string? value) =>
        value?.Replace("<BR>", "\n", StringComparison.OrdinalIgnoreCase);

    private sealed class ReportBuilder(int sequence, Segment obr, ProviderProfile profile)
    {
        private readonly List<ObservationInfo> _observations = [];

        public void Add(ObservationInfo observation) => _observations.Add(observation);

        public ReportInfo Build() => new(
            Sequence: sequence,
            PlacerOrderNumber: obr.Get(2, 1),
            AccessionNumber: obr.Get(profile.AccessionNumber),
            ProcedureCode: obr.Get(4, 1),
            ProcedureDescription: obr.Get(4, 2),
            ProcedureCodingSystem: obr.Get(4, 3),
            ObservationDateTime: obr.Get(7),
            ResultStatus: obr.Get(25),
            Observations: _observations.AsReadOnly());
    }
}
