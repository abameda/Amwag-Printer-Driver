using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Amwag.PrinterSetup.Windows.Network;

/// <summary>Message version field: 0 for SNMPv1 (RFC 1157), 1 for community-based SNMPv2c (RFC 1901).</summary>
internal enum SnmpVersion
{
    V1 = 0,
    V2c = 1,
}

/// <summary>PDU tags (context-specific, constructed) from RFC 3416.</summary>
internal enum SnmpPduType : byte
{
    GetRequest = 0xA0,
    GetNextRequest = 0xA1,
    Response = 0xA2,
    SetRequest = 0xA3,
}

internal enum SnmpValueType
{
    Integer,
    OctetString,
    Null,
    ObjectIdentifier,
    Counter32,
    Gauge32,
    TimeTicks,
    NoSuchObject,
    NoSuchInstance,
    EndOfMibView,
    /// <summary>A type this codec does not interpret (IpAddress, Opaque, Counter64, ...); <see cref="SnmpValue.Tag"/> tells which.</summary>
    Other,
}

/// <summary>
/// A decoded value: <see cref="Text"/> holds OCTET STRING (UTF-8 when valid, otherwise Latin-1, trailing NULs removed) and
/// OBJECT IDENTIFIER (dotted) values; <see cref="Number"/> holds INTEGER, Counter32, Gauge32 and TimeTicks values.
/// </summary>
internal sealed record SnmpValue(SnmpValueType Type, byte Tag)
{
    public string? Text { get; init; }
    public long? Number { get; init; }

    /// <summary>noSuchObject, noSuchInstance or endOfMibView: the agent has no value for this variable.</summary>
    public bool IsException => Type is SnmpValueType.NoSuchObject or SnmpValueType.NoSuchInstance or SnmpValueType.EndOfMibView;
}

internal sealed record SnmpVarBind(string Oid, SnmpValue Value);

internal sealed record SnmpMessage(
    SnmpVersion Version,
    SnmpPduType PduType,
    int RequestId,
    int ErrorStatus,
    int ErrorIndex,
    IReadOnlyList<SnmpVarBind> VarBinds);

/// <summary>
/// BER (X.690) encoding of SNMPv1/v2c GetRequest messages and decoding of SNMP messages (RFC 1157, RFC 1901, RFC 3416,
/// value types from RFC 2578). Decoding is strict about structure (definite lengths only, exact nesting, every length
/// checked against the buffer) and throws <see cref="FormatException"/> for any malformed input.
/// </summary>
internal static class SnmpCodec
{
    internal const int NoError = 0;
    internal const int TooBig = 1;

    private const byte TagInteger = 0x02;
    private const byte TagOctetString = 0x04;
    private const byte TagNull = 0x05;
    private const byte TagObjectIdentifier = 0x06;
    private const byte TagSequence = 0x30;
    private const byte TagCounter32 = 0x41;
    private const byte TagGauge32 = 0x42;
    private const byte TagTimeTicks = 0x43;
    private const byte TagNoSuchObject = 0x80;
    private const byte TagNoSuchInstance = 0x81;
    private const byte TagEndOfMibView = 0x82;

    // RFC 2578 7.1.3: at most 128 sub-identifiers, each at most 2^32-1.
    private const int MaxSubIdentifiers = 128;

    private static readonly string[] ErrorNames =
    [
        "noError", "tooBig", "noSuchName", "badValue", "readOnly", "genErr", "noAccess", "wrongType", "wrongLength",
        "wrongEncoding", "wrongValue", "noCreation", "inconsistentValue", "resourceUnavailable", "commitFailed",
        "undoFailed", "authorizationError", "notWritable", "inconsistentName",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A GetRequest for <paramref name="oids"/> (dotted OBJECT IDENTIFIERs), each bound to NULL.</summary>
    internal static byte[] EncodeGetRequest(SnmpVersion version, string community, int requestId, IReadOnlyList<string> oids)
    {
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(oids);
        if (oids.Count == 0)
            throw new ArgumentException("A GetRequest needs at least one OBJECT IDENTIFIER.", nameof(oids));

        var varBinds = new byte[oids.Count][];
        for (int i = 0; i < oids.Count; i++)
            varBinds[i] = Tlv(TagSequence, Tlv(TagObjectIdentifier, EncodeOid(oids[i])), Tlv(TagNull));

        byte[] pdu = Tlv((byte)SnmpPduType.GetRequest,
            Tlv(TagInteger, EncodeInteger(requestId)),
            Tlv(TagInteger, EncodeInteger(NoError)),
            Tlv(TagInteger, EncodeInteger(0)),
            Tlv(TagSequence, varBinds));

        return Tlv(TagSequence,
            Tlv(TagInteger, EncodeInteger((int)version)),
            Tlv(TagOctetString, Encoding.UTF8.GetBytes(community)),
            pdu);
    }

    internal static SnmpMessage Decode(ReadOnlySpan<byte> datagram)
    {
        var outer = new BerReader(datagram);
        var message = new BerReader(outer.Read(TagSequence, "message"));
        outer.EnsureEnd("message");

        int version = ToInt32(DecodeInteger(message.Read(TagInteger, "version")), "version");
        if (version is not ((int)SnmpVersion.V1 or (int)SnmpVersion.V2c))
            throw new FormatException($"Unsupported SNMP version {version}.");

        message.Read(TagOctetString, "community");
        var pdu = new BerReader(message.Read(out byte pduTag, "PDU"));
        message.EnsureEnd("message");
        if (!Enum.IsDefined((SnmpPduType)pduTag))
            throw new FormatException($"Unsupported PDU type 0x{pduTag:X2}.");

        int requestId = ToInt32(DecodeInteger(pdu.Read(TagInteger, "request-id")), "request-id");
        int errorStatus = ToInt32(DecodeInteger(pdu.Read(TagInteger, "error-status")), "error-status");
        int errorIndex = ToInt32(DecodeInteger(pdu.Read(TagInteger, "error-index")), "error-index");
        if (errorStatus < 0 || errorIndex < 0)
            throw new FormatException($"Negative error-status {errorStatus} or error-index {errorIndex}.");

        var list = new BerReader(pdu.Read(TagSequence, "variable-bindings"));
        pdu.EnsureEnd("PDU");

        var varBinds = new List<SnmpVarBind>();
        while (!list.IsEnd)
        {
            var varBind = new BerReader(list.Read(TagSequence, "variable binding"));
            string name = DecodeOid(varBind.Read(TagObjectIdentifier, "variable name"));
            ReadOnlySpan<byte> content = varBind.Read(out byte tag, "variable value");
            varBind.EnsureEnd("variable binding");
            varBinds.Add(new SnmpVarBind(name, DecodeValue(tag, content)));
        }

        return new SnmpMessage((SnmpVersion)version, (SnmpPduType)pduTag, requestId, errorStatus, errorIndex, varBinds);
    }

    /// <summary>The RFC 3416 name of an error-status value, e.g. "noSuchName".</summary>
    internal static string DescribeError(int errorStatus) =>
        errorStatus >= 0 && errorStatus < ErrorNames.Length ? ErrorNames[errorStatus] : $"error-status {errorStatus}";

    private static SnmpValue DecodeValue(byte tag, ReadOnlySpan<byte> content) => tag switch
    {
        TagInteger => new SnmpValue(SnmpValueType.Integer, tag) { Number = DecodeInteger(content) },
        TagOctetString => new SnmpValue(SnmpValueType.OctetString, tag) { Text = DecodeText(content) },
        TagNull => new SnmpValue(SnmpValueType.Null, tag),
        TagObjectIdentifier => new SnmpValue(SnmpValueType.ObjectIdentifier, tag) { Text = DecodeOid(content) },
        TagCounter32 => new SnmpValue(SnmpValueType.Counter32, tag) { Number = DecodeUnsigned32(content) },
        TagGauge32 => new SnmpValue(SnmpValueType.Gauge32, tag) { Number = DecodeUnsigned32(content) },
        TagTimeTicks => new SnmpValue(SnmpValueType.TimeTicks, tag) { Number = DecodeUnsigned32(content) },
        TagNoSuchObject => new SnmpValue(SnmpValueType.NoSuchObject, tag),
        TagNoSuchInstance => new SnmpValue(SnmpValueType.NoSuchInstance, tag),
        TagEndOfMibView => new SnmpValue(SnmpValueType.EndOfMibView, tag),
        _ => new SnmpValue(SnmpValueType.Other, tag),
    };

    private static string DecodeText(ReadOnlySpan<byte> content)
    {
        int end = content.Length;
        while (end > 0 && content[end - 1] == 0)
            end--;
        content = content[..end];

        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);
        }
    }

    /// <summary>Two's complement, big-endian; non-minimal encodings are tolerated, values beyond 64 bits are not.</summary>
    private static long DecodeInteger(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            throw new FormatException("INTEGER without content octets.");
        if (content.Length > 8)
            throw new FormatException($"INTEGER of {content.Length} octets exceeds 64 bits.");

        long value = (sbyte)content[0];
        for (int i = 1; i < content.Length; i++)
            value = (value << 8) | content[i];
        return value;
    }

    /// <summary>Counter32, Gauge32 and TimeTicks (0..2^32-1). Some agents omit the leading zero octet; their value is read as unsigned.</summary>
    private static long DecodeUnsigned32(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            throw new FormatException("Unsigned value without content octets.");

        int start = 0;
        while (start < content.Length - 1 && content[start] == 0)
            start++;
        if (content.Length - start > 4)
            throw new FormatException("Unsigned value exceeds 32 bits.");

        uint value = 0;
        for (int i = start; i < content.Length; i++)
            value = (value << 8) | content[i];
        return value;
    }

    private static int ToInt32(long value, string field) =>
        value is >= int.MinValue and <= int.MaxValue ? (int)value : throw new FormatException($"{field} {value} is outside the 32-bit range.");

    private static string DecodeOid(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
            throw new FormatException("OBJECT IDENTIFIER without content octets.");

        var text = new StringBuilder(content.Length * 3);
        int offset = 0;
        bool first = true;
        while (offset < content.Length)
        {
            ulong subIdentifier = 0;
            byte octet;
            do
            {
                if (offset >= content.Length)
                    throw new FormatException("OBJECT IDENTIFIER ends inside a sub-identifier.");
                octet = content[offset++];
                subIdentifier = (subIdentifier << 7) | (uint)(octet & 0x7F);
                // The first sub-identifier carries the first two arcs (40 * X + Y), so it may exceed 2^32-1 by up to 80.
                if (subIdentifier > uint.MaxValue + (first ? 80UL : 0UL))
                    throw new FormatException("OBJECT IDENTIFIER sub-identifier exceeds 32 bits.");
            }
            while ((octet & 0x80) != 0);

            if (first)
            {
                ulong firstArc = Math.Min(subIdentifier / 40, 2);
                text.Append(firstArc.ToString(CultureInfo.InvariantCulture)).Append('.')
                    .Append((subIdentifier - (firstArc * 40)).ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            else
            {
                text.Append('.').Append(subIdentifier.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static byte[] EncodeOid(string oid)
    {
        string[] parts = oid.Split('.');
        if (parts.Length is < 2 or > MaxSubIdentifiers)
            throw new ArgumentException($"'{oid}' is not a dotted OBJECT IDENTIFIER with 2 to {MaxSubIdentifiers} arcs.", nameof(oid));

        var arcs = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out arcs[i]))
                throw new ArgumentException($"'{oid}' is not a dotted OBJECT IDENTIFIER.", nameof(oid));
        }

        if (arcs[0] > 2 || (arcs[0] < 2 && arcs[1] > 39))
            throw new ArgumentException($"'{oid}' has an invalid first or second arc.", nameof(oid));

        var content = new List<byte>(parts.Length + 4);
        AppendBase128(content, (arcs[0] * 40UL) + arcs[1]);
        for (int i = 2; i < arcs.Length; i++)
            AppendBase128(content, arcs[i]);
        return [.. content];
    }

    private static void AppendBase128(List<byte> output, ulong value)
    {
        Span<byte> groups = stackalloc byte[10];
        int count = 0;
        do
        {
            groups[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value != 0);

        for (int i = count - 1; i >= 0; i--)
            output.Add((byte)(groups[i] | (i > 0 ? 0x80 : 0)));
    }

    /// <summary>Minimal two's complement (X.690 8.3.2: the first nine bits are never all zeros or all ones).</summary>
    private static byte[] EncodeInteger(long value)
    {
        var octets = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(octets, value);
        int start = 0;
        while (start < 7 && ((octets[start] == 0x00 && (octets[start + 1] & 0x80) == 0) || (octets[start] == 0xFF && (octets[start + 1] & 0x80) != 0)))
            start++;
        return octets[start..];
    }

    /// <summary>Tag, definite length (short form below 128, otherwise long form) and the concatenated contents.</summary>
    private static byte[] Tlv(byte tag, params ReadOnlySpan<byte[]> contents)
    {
        int length = 0;
        foreach (byte[] part in contents)
            length += part.Length;

        int lengthOctets = length < 0x80 ? 0 : length <= 0xFF ? 1 : length <= 0xFFFF ? 2 : length <= 0xFFFFFF ? 3 : 4;
        var result = new byte[2 + lengthOctets + length];
        result[0] = tag;
        if (lengthOctets == 0)
        {
            result[1] = (byte)length;
        }
        else
        {
            result[1] = (byte)(0x80 | lengthOctets);
            for (int i = 0; i < lengthOctets; i++)
                result[2 + i] = (byte)(length >> (8 * (lengthOctets - 1 - i)));
        }

        int offset = 2 + lengthOctets;
        foreach (byte[] part in contents)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>Reads consecutive TLVs from a buffer; every access is bounds-checked and reported as <see cref="FormatException"/>.</summary>
    private ref struct BerReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public BerReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public readonly bool IsEnd => _offset == _data.Length;

        public ReadOnlySpan<byte> Read(byte expectedTag, string element)
        {
            ReadOnlySpan<byte> content = Read(out byte tag, element);
            if (tag != expectedTag)
                throw new FormatException($"Expected {element} (tag 0x{expectedTag:X2}) but found tag 0x{tag:X2}.");
            return content;
        }

        public ReadOnlySpan<byte> Read(out byte tag, string element)
        {
            if (_offset >= _data.Length)
                throw new FormatException($"Truncated message: {element} is missing.");

            tag = _data[_offset++];
            if ((tag & 0x1F) == 0x1F)
                throw new FormatException($"{element} uses the high-tag-number form, which SNMP does not use.");

            int length = ReadLength(element);
            if (length > _data.Length - _offset)
                throw new FormatException($"Truncated message: {element} declares {length} content octets but {_data.Length - _offset} remain.");

            ReadOnlySpan<byte> content = _data.Slice(_offset, length);
            _offset += length;
            return content;
        }

        public readonly void EnsureEnd(string element)
        {
            if (!IsEnd)
                throw new FormatException($"{_data.Length - _offset} unexpected octet(s) after {element}.");
        }

        private int ReadLength(string element)
        {
            if (_offset >= _data.Length)
                throw new FormatException($"Truncated message: the length of {element} is missing.");

            byte first = _data[_offset++];
            if (first < 0x80)
                return first;

            int count = first & 0x7F;
            if (count == 0)
                throw new FormatException($"{element} uses the indefinite length form, which SNMP does not allow.");
            if (count > 4)
                throw new FormatException($"{element} has a {count}-octet length field.");
            if (count > _data.Length - _offset)
                throw new FormatException($"Truncated message: the length of {element} is incomplete.");

            uint length = 0;
            for (int i = 0; i < count; i++)
                length = (length << 8) | _data[_offset++];
            if (length > int.MaxValue)
                throw new FormatException($"{element} declares an impossible length of {length} octets.");
            return (int)length;
        }
    }
}
