using Hl7Receiver.Hl7;

namespace Hl7Receiver.Ingestion;

/// <summary>Result of evaluating one payload: either the extracted report(s) or the reason it isn't acceptable.</summary>
public sealed record Evaluation(MessageHeader Header, ProviderProfile Profile, OruMessage? Oru, Rejection? Rejection, PayloadDecoder.Decoded Decoded)
{
    public bool IsValid => Oru is not null;
}

/// <summary>
/// The pure, in-memory part of the pipeline: decode → sniff header → parse → validate envelope → extract → validate
/// content. No I/O, no side effects, ~1 ms. The receiver runs it synchronously to produce an honest ACK; the worker
/// runs it again from the stored bytes to get the model it persists (so nothing has to survive in memory between the
/// two, and a restart changes nothing).
/// </summary>
public sealed class MessageEvaluator(
    Hl7Parser parser,
    OruExtractor extractor,
    OruValidator validator,
    IProviderProfileRegistry profiles)
{
    public Evaluation Evaluate(byte[] payload)
    {
        var decoded = PayloadDecoder.Decode(payload);
        var header = MessageHeader.Sniff(decoded.Text);
        var profile = profiles.For(header.SendingFacility);

        var parsed = parser.Parse(decoded.Text);
        if (!parsed.Success)
        {
            return new Evaluation(header, profile, null, parsed.Rejection, decoded);
        }

        var envelope = validator.ValidateEnvelope(parsed.Message!, header, profile.Policy);
        if (envelope is not null)
        {
            return new Evaluation(header, profile, null, envelope, decoded);
        }

        var extracted = extractor.Extract(parsed.Message!, header, profile);
        if (!extracted.Success)
        {
            return new Evaluation(header, profile, null, extracted.Rejection, decoded);
        }

        var content = validator.ValidateContent(extracted.Value!, profile.Policy);
        return content is not null
            ? new Evaluation(header, profile, null, content, decoded)
            : new Evaluation(header, profile, extracted.Value, null, decoded);
    }
}
