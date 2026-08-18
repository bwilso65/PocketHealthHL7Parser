using Hl7Receiver.Hl7;
using Microsoft.Extensions.Time.Testing;

namespace Hl7Receiver.Tests;

public class MessageHeaderTests
{
    [Fact]
    public void Sniff_reads_what_it_can_from_a_broken_MSH()
    {
        var header = MessageHeader.Sniff("MSH|^~\\&|RIS|HOSP|POCKET\rPID|1||X\r");

        Assert.Equal("RIS", header.SendingApplication);
        Assert.Equal("HOSP", header.SendingFacility);
        Assert.Equal("POCKET", header.ReceivingApplication);
        Assert.Null(header.MessageControlId);
        Assert.Null(header.MessageType);
        Assert.Null(header.MessageTypeKey);
    }

    [Fact]
    public void Sniff_splits_message_type_and_reads_charset()
    {
        var header = MessageHeader.Sniff("MSH|^~\\&|RIS|WOODBINE|PH|PH|20260101120000||ORU^R01^ORU_R01|MSG1|T|2.5.1||||||8859/1\r");

        Assert.Equal("ORU^R01^ORU_R01", header.MessageType);
        Assert.Equal("ORU", header.MessageCode);
        Assert.Equal("R01", header.TriggerEvent);
        Assert.Equal("ORU^R01", header.MessageTypeKey);
        Assert.Equal("MSG1", header.MessageControlId);
        Assert.Equal("T", header.ProcessingId);
        Assert.Equal("2.5.1", header.VersionId);
        Assert.Equal("8859/1", header.CharacterSet);
    }

    [Fact]
    public void Sniff_honours_non_standard_delimiters()
    {
        var header = MessageHeader.Sniff("MSH#$~\\&#RIS#HOSP#PH#PH#20260101120000##ORU$R01#MSG1#P#2.5\r");

        Assert.Equal("HOSP", header.SendingFacility);
        Assert.Equal("ORU", header.MessageCode);
        Assert.Equal("R01", header.TriggerEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("MSH")]
    [InlineData("{\"json\": true}")]
    public void Sniff_never_throws_on_non_HL7(string text)
    {
        Assert.Equal(MessageHeader.Empty, MessageHeader.Sniff(text));
    }
}

public class AckBuilderTests
{
    private static readonly MessageHeader Original = MessageHeader.Sniff(
        "MSH|^~\\&|RIS|WOODBINE|POCKETHEALTH|POCKETHEALTH|20260101120000||ORU^R01|MSG00001|P|2.5\r");

    private static AckBuilder Builder() =>
        new("POCKETHEALTH", "POCKETHEALTH", new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero)));

    [Fact]
    public void Accept_builds_MSH_and_MSA()
    {
        var ack = Builder().Accept(Original);

        Assert.Equal(
            "MSH|^~\\&|POCKETHEALTH|POCKETHEALTH|RIS|WOODBINE|20260818143000+0000||ACK^R01^ACK|{id}|P|2.5\rMSA|AA|MSG00001|\r",
            ack.Replace(ack.Split('|')[9], "{id}"));
    }

    [Fact]
    public void Reject_adds_ERR_and_escapes_delimiters_in_free_text()
    {
        var ack = Builder().Reject(Original, Rejection.RequiredField("OBX-11", "status | with ^ delimiters & more"));

        var segments = ack.TrimEnd('\r').Split('\r');
        Assert.Equal(3, segments.Length);
        Assert.StartsWith("MSA|AE|MSG00001|OBX-11 (status \\F\\ with \\S\\ delimiters \\T\\ more)", segments[1]);
        Assert.StartsWith("ERR|||101^Required field missing^HL70357|E||||OBX-11", segments[2]);
    }

    [Fact]
    public void Ack_for_unparseable_input_still_has_a_valid_shape()
    {
        var ack = Builder().Reject(MessageHeader.Empty, Rejection.Unparseable("nope"));

        Assert.StartsWith("MSH|^~\\&|POCKETHEALTH|POCKETHEALTH|||20260818143000+0000||ACK|", ack);
        Assert.Contains("|P|2.5\rMSA|AR||nope\r", ack);
        Assert.Contains("ERR|||100^Segment sequence error^HL70357|E||||nope\r", ack);
    }
}

public class Hl7TimestampTests
{
    [Theory]
    [InlineData("20260101115500", "2026-01-01T11:55:00")]
    [InlineData("19850315", "1985-03-15")]
    [InlineData("202601", "2026-01")]
    [InlineData("2026", "2026")]
    [InlineData("2026010111", "2026-01-01T11:00")]
    [InlineData("202601011155", "2026-01-01T11:55")]
    [InlineData("20260101115500.1234", "2026-01-01T11:55:00.1234")]
    [InlineData("20260101115500-0500", "2026-01-01T11:55:00-05:00")]
    [InlineData("20260101115500+0000", "2026-01-01T11:55:00+00:00")]
    public void Converts_valid_timestamps_preserving_precision(string hl7, string iso)
    {
        Assert.Equal(iso, Hl7Timestamp.ToIso8601(hl7));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-01-01")]
    [InlineData("20261301")]       // month 13
    [InlineData("20260132")]       // day 32
    [InlineData("20260101250000")] // hour 25
    public void Returns_null_for_invalid_timestamps(string? hl7)
    {
        Assert.Null(Hl7Timestamp.ToIso8601(hl7));
    }
}

public class ProviderProfileRegistryTests
{
    [Fact]
    public void Unknown_and_null_senders_get_the_default_profile()
    {
        var registry = new ProviderProfileRegistry();

        Assert.Same(ProviderProfile.Default, registry.For("WOODBINE"));
        Assert.Same(ProviderProfile.Default, registry.For(null));
    }

    [Fact]
    public void Overrides_match_case_insensitively()
    {
        var quirky = ProviderProfile.Default with { Name = "quirky" };
        var registry = new ProviderProfileRegistry(new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase) { ["QUIRKY"] = quirky });

        Assert.Same(quirky, registry.For("quirky"));
        Assert.Same(ProviderProfile.Default, registry.For("OTHER"));
    }
}
