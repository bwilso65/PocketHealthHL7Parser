using Efferent.HL7.V2;

namespace Hl7Receiver.Hl7;

/// <summary>
/// The strictness policy, applied in two passes:
/// <list type="number">
///   <item><see cref="ValidateEnvelope"/> — before extraction: is this a single message of a type we accept, from an identifiable sender?</item>
///   <item><see cref="ValidateContent"/> — after extraction: does it carry the fields a report needs to be safely usable?</item>
/// </list>
/// Returns the first rejection found (senders fix one thing at a time; the raw payload is kept for the rest).
/// </summary>
public sealed class OruValidator
{
    public Rejection? ValidateEnvelope(Message message, MessageHeader header, ValidationPolicy policy)
    {
        // Two MSH segments = two messages glued together (a known sender bug). We refuse the whole payload rather than
        // guess: one request must map to one ACK for one control ID. The raw bytes are stored so nothing is lost.
        var mshCount = message.CountOf("MSH");
        if (mshCount > 1)
        {
            return Rejection.MultipleMsh(mshCount);
        }

        var typeKey = header.MessageTypeKey;
        if (typeKey is null || !policy.AcceptedMessageTypes.Contains(typeKey))
        {
            return Rejection.UnsupportedMessageType(header.MessageType);
        }

        if (policy.RequireSendingFacility && string.IsNullOrWhiteSpace(header.SendingFacility))
        {
            return Rejection.RequiredField("MSH-4", "sending facility");
        }

        return null;
    }

    public Rejection? ValidateContent(OruMessage oru, ValidationPolicy policy)
    {
        var patient = oru.Patient;

        if (policy.RequirePatientIdentifier && patient.Identifier is null)
        {
            return Rejection.RequiredField("PID-3", "patient identifier");
        }

        if (policy.RequirePatientName && patient.FamilyName is null)
        {
            return Rejection.RequiredField("PID-5", "patient name");
        }

        foreach (var report in oru.Reports)
        {
            var label = report.AccessionNumber ?? $"OBR #{report.Sequence}";

            if (policy.RequireAccessionNumber && report.AccessionNumber is null)
            {
                return Rejection.RequiredField($"OBR-3 (OBR #{report.Sequence})", "accession / filler order number");
            }

            if (policy.RequireAtLeastOneObservation && report.Observations.Count == 0)
            {
                return Rejection.NoObservations(label);
            }

            if (policy.RequireObservationResultStatus)
            {
                foreach (var obx in report.Observations)
                {
                    if (obx.ResultStatus is null)
                    {
                        // The most common way this happens in practice is a truncated message: the last OBX was cut
                        // mid-field, so its trailing fields (including OBX-11) never arrived.
                        return Rejection.RequiredField($"OBX-11 (OBX #{obx.SetId?.ToString() ?? "?"} of {label})", "observation result status");
                    }
                }
            }
        }

        return null;
    }
}
