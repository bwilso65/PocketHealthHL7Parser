using System.Text;

namespace Hl7Receiver.Hl7;

/// <summary>
/// Builds the HL7 acknowledgement returned to the sender the moment its bytes are safely stored.
///
/// Processing is asynchronous, so the ACK is a *receipt*, not a verdict: <c>MSA-1 = AA</c> with MSA-3 "Received".
/// (HL7's precise term for "committed to safe storage, application processing pending" is <c>CA</c>, but that code
/// only exists in enhanced-acknowledgement mode — MSH-15 set — and Woodbine sends original mode, where the sender
/// expects <c>AA</c>. Standard interface-engine behaviour is to ACK <c>AA</c> on receipt and handle later processing
/// failures on the receiver's side, which is what we do.)
///
/// Built by hand rather than with the library so it also works when the inbound message is unparseable —
/// <see cref="MessageHeader.Sniff"/> gives us whatever MSH fields were readable.
/// </summary>
public sealed class AckBuilder(string ourApplication = "POCKETHEALTH", string ourFacility = "POCKETHEALTH", TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Received(MessageHeader original, string note = "Received")
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

        sb.Append("MSA|AA|")
          .Append(Escape(original.MessageControlId)).Append('|')
          .Append(Escape(note))
          .Append('\r');

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
