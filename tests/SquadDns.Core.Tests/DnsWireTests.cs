using System.Buffers.Binary;
using SquadDns.Core.Testing;

namespace SquadDns.Core.Tests;

public class DnsWireTests
{
    [Fact]
    public void BuildQuery_writes_header_labels_and_question()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A);

        Assert.Equal(29, query.Length);
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6)));

        Assert.Equal(7, query[12]);
        Assert.Equal((byte)'e', query[13]);
        Assert.Equal(3, query[20]);
        Assert.Equal(0, query[24]);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(25)));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(27)));
    }

    [Fact]
    public void BuildQuery_ignores_a_trailing_dot()
    {
        Assert.Equal(
            DnsWire.BuildQuery("example.com", DnsRecordType.A),
            DnsWire.BuildQuery("example.com.", DnsRecordType.A));
    }

    [Fact]
    public void BuildQuery_can_disable_recursion_desired()
    {
        var query = DnsWire.BuildQuery("a.test", DnsRecordType.A, id: 42, recursionDesired: false);

        Assert.Equal(42, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0)));
        Assert.Equal(0x0000, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2)));
    }

    [Fact]
    public void BuildQuery_rejects_empty_name_and_oversized_label()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery("  ", DnsRecordType.A));
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(new string('a', 64) + ".test", DnsRecordType.A));
    }

    [Fact]
    public void Parse_reads_a_compressed_A_answer()
    {
        var response = DnsWire.Parse(AnswerMessage(flags: 0x8180, type: 1, data: new byte[] { 93, 184, 216, 34 }));

        Assert.Equal(0x1234, response.Id);
        Assert.False(response.IsQuery);
        Assert.Equal(0, response.RCode);
        Assert.False(response.Truncated);
        Assert.Equal(1, response.AnswerCount);
        Assert.Equal(new[] { "93.184.216.34" }, response.Answers);
    }

    [Fact]
    public void Parse_reads_a_AAAA_answer()
    {
        var data = new byte[16];
        data[0] = 0x26;
        data[1] = 0x06;
        data[15] = 0x01;

        var response = DnsWire.Parse(AnswerMessage(flags: 0x8180, type: 28, data: data));

        Assert.Single(response.Answers);
        Assert.StartsWith("2606:", response.Answers[0]);
    }

    [Fact]
    public void Parse_surfaces_status_flags()
    {
        Assert.Equal(3, DnsWire.Parse(AnswerMessage(0x8183, 1, new byte[] { 1, 2, 3, 4 })).RCode);
        Assert.True(DnsWire.Parse(AnswerMessage(0x8380, 1, new byte[] { 1, 2, 3, 4 })).Truncated);
        Assert.True(DnsWire.Parse(DnsWire.BuildQuery("example.com", DnsRecordType.A)).IsQuery);
    }

    [Fact]
    public void Parse_never_throws_on_truncated_input()
    {
        Assert.Empty(DnsWire.Parse(Array.Empty<byte>()).Answers);
        Assert.Empty(DnsWire.Parse(new byte[] { 0x12, 0x34 }).Answers);
        Assert.Empty(DnsWire.Parse(AnswerMessage(0x8180, 1, new byte[] { 1, 2, 3 })).Answers);
    }

    [Fact]
    public void Base64Url_round_trips_without_padding_or_reserved_chars()
    {
        var data = new byte[] { 0xFB, 0xFF, 0xFE, 0x00, 0x01 };
        var text = DnsWire.Base64UrlEncode(data);

        Assert.DoesNotContain('=', text);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.Equal(data, DnsWire.Base64UrlDecode(text));
    }

    private static byte[] AnswerMessage(ushort flags, ushort type, byte[] data)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(6), 1);
        stream.Write(header);

        stream.WriteByte(7);
        stream.Write("example"u8);
        stream.WriteByte(3);
        stream.Write("com"u8);
        stream.WriteByte(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, type);
        BinaryPrimitives.WriteUInt16BigEndian(question.Slice(2), 1);
        stream.Write(question);

        stream.WriteByte(0xC0);
        stream.WriteByte(0x0C);
        Span<byte> record = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(record, type);
        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(record.Slice(4), 60);
        stream.Write(record);
        stream.WriteByte((byte)(data.Length >> 8));
        stream.WriteByte((byte)(data.Length & 0xFF));
        stream.Write(data);

        return stream.ToArray();
    }
}
