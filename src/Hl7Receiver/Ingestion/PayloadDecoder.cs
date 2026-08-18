using System.Text;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// Turns request bytes into text without silently corrupting characters.
/// Order of preference:
///   1. MSH-18 if the sender declared a charset we know (UNICODE UTF-8, 8859/1, ASCII);
///   2. strict UTF-8 (the assumption for Woodbine — Maya doesn't know their encoding, but their samples are UTF-8);
///   3. ISO-8859-1 as a fallback if the bytes aren't valid UTF-8 (every byte sequence is valid Latin-1, so
///      accented names from a Windows-1252 sender come through recognizably instead of as U+FFFD).
/// The raw bytes are stored regardless, so a wrong guess is recoverable.
/// </summary>
public static class PayloadDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public sealed record Decoded(string Text, string Charset, bool UsedFallback);

    public static Decoded Decode(ReadOnlySpan<byte> bytes)
    {
        // MSH is ASCII-compatible in every charset we support, so peeking at it via Latin-1 is safe.
        var declared = Hl7.MessageHeader.Sniff(Encoding.Latin1.GetString(bytes)).CharacterSet?.Trim().ToUpperInvariant();

        switch (declared)
        {
            case "UNICODE UTF-8":
            case "UTF-8":
            case "UNICODE":
                return new Decoded(Encoding.UTF8.GetString(bytes), "UTF-8 (declared)", false);
            case "8859/1":
            case "ISO-8859-1":
            case "ASCII":
                return new Decoded(Encoding.Latin1.GetString(bytes), "ISO-8859-1 (declared)", false);
        }

        try
        {
            return new Decoded(StrictUtf8.GetString(bytes), "UTF-8", false);
        }
        catch (DecoderFallbackException)
        {
            return new Decoded(Encoding.Latin1.GetString(bytes), "ISO-8859-1 (fallback: not valid UTF-8)", true);
        }
    }
}
