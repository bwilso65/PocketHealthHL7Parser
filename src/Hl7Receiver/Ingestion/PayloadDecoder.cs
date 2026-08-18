using System.Text;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// Turns request bytes into text without silently corrupting characters.
/// Order of preference:
///   1. a byte-order mark, if present (UTF-8, UTF-16 LE/BE — Windows tooling adds these);
///   2. MSH-18 if the sender declared a charset we know (UNICODE UTF-8, 8859/1, ASCII);
///   3. strict UTF-8 (the assumption for Woodbine — Maya doesn't know their encoding, but their samples are UTF-8);
///   4. ISO-8859-1 as a fallback if the bytes aren't valid UTF-8 (every byte sequence is valid Latin-1, so
///      accented names from a Windows-1252 sender come through recognizably instead of as U+FFFD).
/// Leading whitespace/blank lines before MSH are dropped (transport noise, not content). The raw bytes are stored
/// exactly as received regardless, so a wrong guess is recoverable.
/// </summary>
public static class PayloadDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly char[] LeadingNoise = [' ', '\t', '\r', '\n', (char)0xFEFF];

    public sealed record Decoded(string Text, string Charset, bool UsedFallback);

    public static Decoded Decode(ReadOnlySpan<byte> bytes)
    {
        // Byte-order marks
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Finish(Encoding.UTF8.GetString(bytes[3..]), "UTF-8 (BOM)", false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Finish(Encoding.Unicode.GetString(bytes[2..]), "UTF-16LE (BOM)", false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Finish(Encoding.BigEndianUnicode.GetString(bytes[2..]), "UTF-16BE (BOM)", false);
        }

        // MSH is ASCII-compatible in every charset we support, so peeking at it via Latin-1 is safe.
        var declared = Hl7.MessageHeader.Sniff(Encoding.Latin1.GetString(bytes).TrimStart(LeadingNoise)).CharacterSet?.Trim().ToUpperInvariant();

        switch (declared)
        {
            case "UNICODE UTF-8":
            case "UTF-8":
            case "UNICODE":
                return Finish(Encoding.UTF8.GetString(bytes), "UTF-8 (declared)", false);
            case "8859/1":
            case "ISO-8859-1":
            case "ASCII":
                return Finish(Encoding.Latin1.GetString(bytes), "ISO-8859-1 (declared)", false);
        }

        try
        {
            return Finish(StrictUtf8.GetString(bytes), "UTF-8", false);
        }
        catch (DecoderFallbackException)
        {
            return Finish(Encoding.Latin1.GetString(bytes), "ISO-8859-1 (fallback: not valid UTF-8)", true);
        }
    }

    private static Decoded Finish(string text, string charset, bool fallback) =>
        new(text.TrimStart(LeadingNoise), charset, fallback);
}
