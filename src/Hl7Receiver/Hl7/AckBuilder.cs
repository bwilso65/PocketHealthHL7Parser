using System.Text;

namespace Hl7Receiver.Hl7;

/// <summary>
/// Builds the HL7 acknowledgement returned to the sender (MSH + MSA [+ ERR]). Built by hand rather than with the
/// library so it also works when the inbound message did not parse — <see cref="MessageHeader.Sniff"/> gives us
/// whatever MSH fields were readable, and the ACK is as informative as the input allowed.
/// </summary>
public sealed class AckBuilder(string ourApplication = "POCKETHEALTH", string ourFacility = "POCKETHEALTH", TimeProvider? clock = null)
{
    private static readonly IReadOnlyDictionary<string, string> Table0357 = new Dictionary<string, string>
    {
        ["0"] = "Message accepted",
        ["100"] = "Segment sequence error",
        ["101"] = "Required field missing",
        ["102"] = "Data type error",
        ["103"] = "Table value not found",
        ["200"] = "Unsupported message type",
        ["201"] = "Unsupported event code",
        ["202"] = "Unsupported processing id",
        ["203"] = "Unsupported version id",
        ["204"] = "Unknown key identifier",
        ["205"] = "Duplicate key identifier",
        ["206"] = "Application record locked",
        ["207"] = "Application internal error",
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Positive acknowledgement. <paramref name="note"/> goes to MSA-3 (e.g. "duplicate; not reprocessed").</summary>
    public string Accept(MessageHeader original, string? note = null) =>
        Build(original, AckCode.AA, note, hl7ErrorCode: null);

    /// <summary>Negative acknowledgement carrying the rejection reason in MSA-3 and an ERR segment.</summary>
    public string Reject(MessageHeader original, Rejection rejection) =>
        Build(original, rejection.Ack, rejection.Detail, rejection.Hl7ErrorCode);

    private string Build(MessageHeader original, AckCode code, string? text, string? hl7ErrorCode)
    {
        var now = _clock.GetUtcNow().ToString("yyyyMMddHHmmss") + "+0000";
        var messageType = original.TriggerEvent is null ? "ACK" : $"ACK^{Escape(original.TriggerEvent)}^ACK";
        var ackControlId = Guid.NewGuid().ToString("N")[..20];

        var sb = new StringBuilder();
        sb.Append("MSH|^~\\&|")
          .Append(Escape(ourApplication)).Append('|')
          .Append(Escape(ourFacility)).Append('|')
          .Append(Escape(original.SendingApplication)).Append('|')
          .Append(Escape(original.SendingFacility)).Append('|')
          .Append(now).Append("||")
          .Append(messageType).Append('|')
          .Append(ackControlId).Append('|')
          .Append(Escape(original.ProcessingId ?? "P")).Append('|')
          .Append(Escape(original.VersionId ?? "2.5"))
          .Append('\r');

        sb.Append("MSA|").Append(code).Append('|')
          .Append(Escape(original.MessageControlId)).Append('|')
          .Append(Escape(text))
          .Append('\r');

        if (hl7ErrorCode is not null)
        {
            var description = Table0357.TryGetValue(hl7ErrorCode, out var d) ? d : "Error";
            // ERR-3: HL7 error code (table 0357); ERR-4: severity (E = error); ERR-8: user message
            sb.Append("ERR|||").Append(hl7ErrorCode).Append('^').Append(Escape(description)).Append("^HL70357|E||||")
              .Append(Escape(text))
              .Append('\r');
        }

        return sb.ToString();
    }

    /// <summary>Escape HL7 delimiters in free text and strip segment terminators.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\E\\"); break;
                case '|': sb.Append("\\F\\"); break;
                case '^': sb.Append("\\S\\"); break;
                case '~': sb.Append("\\R\\"); break;
                case '&': sb.Append("\\T\\"); break;
                case '\r':
                case '\n': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
