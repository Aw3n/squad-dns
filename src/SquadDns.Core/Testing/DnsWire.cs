using System.Buffers.Binary;
using System.Text;

namespace SquadDns.Core.Testing;

public static class DnsRecordType
{
    public const ushort A = 1;
    public const ushort AAAA = 28;
}

public sealed record DnsResponse(ushort Id, ushort Flags, int AnswerCount, IReadOnlyList<string> Answers, bool IsQuery)
{
    public int RCode => Flags & 0x000F;
    public bool Truncated => (Flags & 0x0200) != 0;
    public bool AuthoritativeAnswer => (Flags & 0x0400) != 0;
}

public static class DnsWire
{
    public static byte[] BuildQuery(string name, ushort type, ushort id = 0, bool recursionDesired = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name required", nameof(name));
        }

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        var flags = (ushort)(recursionDesired ? 0x0100 : 0x0000);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4), 1);
        stream.Write(header);

        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"label too long: {label}", nameof(name));
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(trailer, type);
        BinaryPrimitives.WriteUInt16BigEndian(trailer.Slice(2), 1);
        stream.Write(trailer);

        return stream.ToArray();
    }

    public static DnsResponse Parse(byte[] message)
    {
        if (message.Length < 12)
        {
            return new DnsResponse(0, 0, 0, Array.Empty<string>(), IsQuery: false);
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(0));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));

        var position = 12;
        for (var q = 0; q < questionCount && position < message.Length; q++)
        {
            position = SkipName(message, position);
            position += 4;
        }

        var answers = new List<string>();
        for (var a = 0; a < answerCount && position < message.Length; a++)
        {
            position = SkipName(message, position);
            if (position + 10 > message.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(position));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(position + 8));
            position += 10;

            if (position + dataLength > message.Length)
            {
                break;
            }

            var data = message.AsSpan(position, dataLength);
            switch (type)
            {
                case 1 when dataLength == 4:
                    answers.Add($"{data[0]}.{data[1]}.{data[2]}.{data[3]}");
                    break;
                case 28 when dataLength == 16:
                    answers.Add(new System.Net.IPAddress(data.ToArray()).ToString());
                    break;
                case 5:
                    answers.Add("<cname>");
                    break;
            }

            position += dataLength;
        }

        return new DnsResponse(id, flags, answerCount, answers, IsQuery: (flags & 0x8000) == 0);
    }

    private static int SkipName(byte[] message, int position)
    {
        var hops = 0;
        while (position < message.Length && hops++ < 12)
        {
            var length = message[position];
            if (length == 0)
            {
                return position + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return position + 2;
            }

            position += 1 + length;
        }

        return message.Length;
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.Length % 4 == 0 ? padded : padded.PadRight(padded.Length + (4 - padded.Length % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
