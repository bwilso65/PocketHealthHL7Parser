using Efferent.HL7.V2;

namespace Hl7Receiver.Hl7;

/// <summary>Outcome of syntactic parsing: either a parsed <see cref="Efferent.HL7.V2.Message"/> or a rejection.</summary>
public sealed record ParseOutcome(Message? Message, Rejection? Rejection)
{
    public bool Success => Message is not null;
}

/// <summary>
/// Thin wrapper over the HL7-V2 library's parser. The library is deliberately schema-free; what it enforces here is
/// only syntax: starts with MSH, MSH has its required fields (9/10/11/12), segment names are well-formed, every
/// segment uses the declared field separator. Everything semantic (message type, required clinical fields,
/// structure) is decided by <see cref="OruValidator"/> and <see cref="OruExtractor"/>, where the policy is ours.
/// </summary>
public sealed class Hl7Parser
{
    public ParseOutcome Parse(string text)
    {
        var message = new Message(text);
        try
        {
            if (!message.ParseMessage())
            {
                return new ParseOutcome(null, Rejection.Unparseable("Message could not be parsed (round-trip check failed)"));
            }
        }
        catch (HL7Exception ex)
        {
            return new ParseOutcome(null, Rejection.Unparseable(CleanLibraryError(ex.Message)));
        }

        return new ParseOutcome(message, null);
    }

    /// <summary>The library nests messages ("Failed to validate the message with error - Failed to ... - MSH ..."); keep the innermost, most specific part.</summary>
    private static string CleanLibraryError(string raw)
    {
        const string separator = " - ";
        var idx = raw.LastIndexOf(separator, StringComparison.Ordinal);
        var detail = idx >= 0 ? raw[(idx + separator.Length)..] : raw;
        return string.IsNullOrWhiteSpace(detail) ? raw : detail.Trim().TrimEnd('.');
    }
}
