using Amwag.PrinterSetup.Windows.Network;

namespace Amwag.PrinterSetup.Windows.Tests.Network;

public sealed class SnmpCodecTests
{
    private const string SysDescr = "1.3.6.1.2.1.1.1.0";
    private const string SysObjectId = "1.3.6.1.2.1.1.2.0";

    [Fact]
    public void Encodes_v1_get_of_sysDescr_exactly()
    {
        // Derived by hand from RFC 1157 and X.690 (all lengths below 128, so every length is one short-form octet):
        //   30 26                                  Message SEQUENCE, 38 content octets = 3 + 8 + 27
        //     02 01 00                             version INTEGER 0 (version-1)
        //     04 06 70 75 62 6C 69 63              community OCTET STRING "public"
        //     A0 19                                GetRequest-PDU [0] IMPLICIT SEQUENCE, 25 octets = 3 + 3 + 3 + 16
        //       02 01 01                           request-id INTEGER 1
        //       02 01 00                           error-status INTEGER 0 (noError)
        //       02 01 00                           error-index INTEGER 0
        //       30 0E                              variable-bindings SEQUENCE OF, 14 octets
        //         30 0C                            VarBind SEQUENCE, 12 octets = 10 + 2
        //           06 08 2B 06 01 02 01 01 01 00  name 1.3.6.1.2.1.1.1.0: first octet 40 * 1 + 3 = 43 (0x2B), then
        //                                          6, 1, 2, 1, 1, 1, 0, each below 128 and so one octet
        //           05 00                          value NULL (unSpecified in retrieval requests)
        byte[] expected =
        [
            0x30, 0x26,
            0x02, 0x01, 0x00,
            0x04, 0x06, 0x70, 0x75, 0x62, 0x6C, 0x69, 0x63,
            0xA0, 0x19,
            0x02, 0x01, 0x01,
            0x02, 0x01, 0x00,
            0x02, 0x01, 0x00,
            0x30, 0x0E,
            0x30, 0x0C,
            0x06, 0x08, 0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x00,
            0x05, 0x00,
        ];

        byte[] actual = SnmpCodec.EncodeGetRequest(SnmpVersion.V1, "public", 1, [SysDescr]);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Encodes_v2c_with_version_one()
    {
        byte[] v1 = SnmpCodec.EncodeGetRequest(SnmpVersion.V1, "public", 1, [SysDescr]);
        byte[] v2c = SnmpCodec.EncodeGetRequest(SnmpVersion.V2c, "public", 1, [SysDescr]);

        Assert.Equal(0x01, v2c[4]);
        Assert.Equal(v1[..4], v2c[..4]);
        Assert.Equal(v1[5..], v2c[5..]);
    }

    [Theory]
    [InlineData(127, new byte[] { 0x02, 0x01, 0x7F })]
    [InlineData(128, new byte[] { 0x02, 0x02, 0x00, 0x80 })]
    [InlineData(int.MaxValue, new byte[] { 0x02, 0x04, 0x7F, 0xFF, 0xFF, 0xFF })]
    public void Encodes_request_id_minimally(int requestId, byte[] expectedInteger)
    {
        byte[] message = SnmpCodec.EncodeGetRequest(SnmpVersion.V1, "public", requestId, [SysDescr]);

        // The request-id is the first element of the PDU: after the 2-octet message header, version (3) and community (8)
        // and the 2-octet PDU header.
        Assert.Equal(expectedInteger, message[15..(15 + expectedInteger.Length)]);
    }

    [Fact]
    public void Round_trips_a_long_request_with_long_form_lengths()
    {
        string[] oids = Enumerable.Range(1, 12).Select(i => $"1.3.6.1.4.1.2699.1.2.1.2.1.1.3.{i}").Append("2.999.3").ToArray();

        byte[] encoded = SnmpCodec.EncodeGetRequest(SnmpVersion.V2c, "public", 123456789, oids);
        SnmpMessage decoded = SnmpCodec.Decode(encoded);

        // Over 255 content octets: the message length takes the two-octet long form 0x82 hi lo.
        Assert.Equal(0x82, encoded[1]);
        Assert.Equal(encoded.Length - 4, (encoded[2] << 8) | encoded[3]);
        Assert.Equal(SnmpVersion.V2c, decoded.Version);
        Assert.Equal(SnmpPduType.GetRequest, decoded.PduType);
        Assert.Equal(123456789, decoded.RequestId);
        Assert.Equal(oids, decoded.VarBinds.Select(v => v.Oid));
        Assert.All(decoded.VarBinds, v => Assert.Equal(SnmpValueType.Null, v.Value.Type));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("3.1")]
    [InlineData("1.40")]
    [InlineData("1..2")]
    [InlineData("1.2.a")]
    [InlineData("1.2.-3")]
    [InlineData(" 1.3.6")]
    [InlineData("1.3.6.4294967296")]
    public void Rejects_invalid_oids_when_encoding(string oid)
    {
        Assert.Throws<ArgumentException>(() => SnmpCodec.EncodeGetRequest(SnmpVersion.V1, "public", 1, [oid]));
    }

    [Fact]
    public void Decodes_octet_string_response()
    {
        byte[] message = Ber.Response(1, 0x12345678, Ber.VarBind(SysDescr, Ber.OctetString("KYOCERA Document Solutions Printing System")));

        SnmpMessage decoded = SnmpCodec.Decode(message);

        Assert.Equal(SnmpVersion.V2c, decoded.Version);
        Assert.Equal(SnmpPduType.Response, decoded.PduType);
        Assert.Equal(0x12345678, decoded.RequestId);
        Assert.Equal(0, decoded.ErrorStatus);
        Assert.Equal(0, decoded.ErrorIndex);
        SnmpVarBind varBind = Assert.Single(decoded.VarBinds);
        Assert.Equal(SysDescr, varBind.Oid);
        Assert.Equal(SnmpValueType.OctetString, varBind.Value.Type);
        Assert.Equal("KYOCERA Document Solutions Printing System", varBind.Value.Text);
    }

    [Fact]
    public void Decodes_object_identifier_value_with_multi_octet_sub_identifier()
    {
        byte[] message = Ber.Response(0, 7, Ber.VarBind(SysObjectId, Ber.Oid("1.3.6.1.4.1.1347.41")));
        // The value is the last TLV: 43 (1.3), 6, 1, 4, 1, then 1347 = 10 * 128 + 67 as 0x8A 0x43, then 41 = 0x29.
        Assert.Equal(new byte[] { 0x06, 0x08, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x8A, 0x43, 0x29 }, message[^10..]);

        SnmpValue value = Assert.Single(SnmpCodec.Decode(message).VarBinds).Value;

        Assert.Equal(SnmpValueType.ObjectIdentifier, value.Type);
        Assert.Equal("1.3.6.1.4.1.1347.41", value.Text);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0L)]
    [InlineData(new byte[] { 0x7F }, 127L)]
    [InlineData(new byte[] { 0x00, 0x80 }, 128L)]
    [InlineData(new byte[] { 0xFF }, -1L)]
    [InlineData(new byte[] { 0xFF, 0x7F }, -129L)]
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x00 }, (long)int.MinValue)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01 }, 1L)]
    public void Decodes_integer_values(byte[] content, long expected)
    {
        SnmpValue value = DecodeSingleValue(Ber.Tlv(0x02, content));

        Assert.Equal(SnmpValueType.Integer, value.Type);
        Assert.Equal(expected, value.Number);
    }

    [Theory]
    [InlineData(0x41, new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF }, nameof(SnmpValueType.Counter32), 4294967295L)]
    [InlineData(0x42, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, nameof(SnmpValueType.Gauge32), 4294967295L)]
    [InlineData(0x43, new byte[] { 0x01, 0x00 }, nameof(SnmpValueType.TimeTicks), 256L)]
    [InlineData(0x43, new byte[] { 0x00 }, nameof(SnmpValueType.TimeTicks), 0L)]
    public void Decodes_unsigned_values(byte tag, byte[] content, string type, long expected)
    {
        SnmpValue value = DecodeSingleValue(Ber.Tlv(tag, content));

        Assert.Equal(Enum.Parse<SnmpValueType>(type), value.Type);
        Assert.Equal(expected, value.Number);
    }

    [Theory]
    [InlineData(0x80, nameof(SnmpValueType.NoSuchObject))]
    [InlineData(0x81, nameof(SnmpValueType.NoSuchInstance))]
    [InlineData(0x82, nameof(SnmpValueType.EndOfMibView))]
    public void Decodes_exception_values(byte tag, string type)
    {
        SnmpValue value = DecodeSingleValue([tag, 0x00]);

        Assert.Equal(Enum.Parse<SnmpValueType>(type), value.Type);
        Assert.True(value.IsException);
        Assert.Null(value.Text);
    }

    [Fact]
    public void Decodes_null_and_uninterpreted_types()
    {
        SnmpValue nullValue = DecodeSingleValue(Ber.Null);
        SnmpValue ipAddress = DecodeSingleValue([0x40, 0x04, 0xC0, 0xA8, 0x01, 0x14]);

        Assert.Equal(SnmpValueType.Null, nullValue.Type);
        Assert.False(nullValue.IsException);
        Assert.Equal(SnmpValueType.Other, ipAddress.Type);
        Assert.Equal(0x40, ipAddress.Tag);
    }

    [Fact]
    public void Decodes_long_form_lengths()
    {
        string longText = new('A', 300);
        byte[] value = Ber.OctetString(longText);
        byte[] message = Ber.Response(1, 42, Ber.VarBind(SysDescr, value));

        // 300 content octets need the two-octet long form (0x82 0x01 0x2C); the enclosing SEQUENCEs use it too.
        Assert.Equal(new byte[] { 0x04, 0x82, 0x01, 0x2C }, value[..4]);
        Assert.Equal(0x82, message[1]);

        Assert.Equal(longText, Assert.Single(SnmpCodec.Decode(message).VarBinds).Value.Text);
    }

    [Fact]
    public void Decodes_one_octet_long_form_length()
    {
        // 200 content octets: 0x81 0xC8.
        byte[] value = [0x04, 0x81, 0xC8, .. Enumerable.Repeat((byte)'x', 200)];

        Assert.Equal(new string('x', 200), DecodeSingleValue(value).Text);
    }

    [Theory]
    [InlineData(new byte[] { 0xC3, 0xA9, 0x74, 0xC3, 0xA9 }, "été")]
    [InlineData(new byte[] { 0xE9, 0x74, 0xE9 }, "été")]
    [InlineData(new byte[] { 0x41, 0x42, 0x43, 0x00, 0x00 }, "ABC")]
    [InlineData(new byte[] { 0x00 }, "")]
    public void Decodes_text_as_utf8_or_latin1_without_trailing_nuls(byte[] raw, string expected)
    {
        Assert.Equal(expected, DecodeSingleValue(Ber.OctetString(raw)).Text);
    }

    [Theory]
    [MemberData(nameof(MalformedMessages))]
    public void Rejects_malformed_messages(string reason, byte[] message)
    {
        var ex = Assert.Throws<FormatException>(() => SnmpCodec.Decode(message));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), reason);
    }

    public static TheoryData<string, byte[]> MalformedMessages()
    {
        byte[] valid = ValidResponse();
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "tag without length", [0x30] },
            { "length beyond buffer", [0x30, 0x05, 0x02, 0x01] },
            { "indefinite length", [0x30, 0x80, 0x02, 0x01, 0x00, 0x00, 0x00] },
            { "five length octets", [0x30, 0x85, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00] },
            { "impossible length", [0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF] },
            { "incomplete long length", [0x30, 0x82, 0x01] },
            { "wrong outer tag", [0x31, 0x00] },
            { "high tag number form", [0x3F, 0x01, 0x00] },
            { "trailing data", [.. valid, 0x00] },
            { "unsupported version", Ber.Message(3, "public", 0xA2, 1, 0, 0, Ber.VarBind(SysDescr, Ber.Null)) },
            { "SNMPv1 trap PDU", Ber.Message(0, "public", 0xA4, 1, 0, 0) },
            { "empty request-id", Ber.Tlv(0x30, Ber.Integer(0), Ber.OctetString("public"), Ber.Tlv(0xA2, [0x02, 0x00], Ber.Integer(0), Ber.Integer(0), Ber.Tlv(0x30))) },
            { "request-id above 2^31-1", Ber.Tlv(0x30, Ber.Integer(0), Ber.OctetString("public"), Ber.Tlv(0xA2, Ber.Integer(2147483648L), Ber.Integer(0), Ber.Integer(0), Ber.Tlv(0x30))) },
            { "negative error-status", Ber.Message(1, "public", 0xA2, 1, -1, 0) },
            { "missing variable-bindings", Ber.Tlv(0x30, Ber.Integer(0), Ber.OctetString("public"), Ber.Tlv(0xA2, Ber.Integer(1), Ber.Integer(0), Ber.Integer(0))) },
            { "varbind is not a SEQUENCE", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x31, Ber.Oid(SysDescr), Ber.Null)) },
            { "varbind without value", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, Ber.Oid(SysDescr))) },
            { "varbind with extra element", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, Ber.Oid(SysDescr), Ber.Null, Ber.Null)) },
            { "name is not an OID", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, Ber.OctetString("1.3"), Ber.Null)) },
            { "empty OID", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, [0x06, 0x00], Ber.Null)) },
            { "OID ends inside a sub-identifier", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, [0x06, 0x02, 0x2B, 0x86], Ber.Null)) },
            { "OID sub-identifier above 2^32-1", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, [0x06, 0x06, 0x2B, 0x90, 0x80, 0x80, 0x80, 0x00], Ber.Null)) },
            { "INTEGER value above 64 bits", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.VarBind(SysDescr, [0x02, 0x09, 0x01, 0, 0, 0, 0, 0, 0, 0, 0])) },
            { "Counter32 above 32 bits", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.VarBind(SysDescr, [0x41, 0x05, 0x01, 0, 0, 0, 0])) },
            { "empty TimeTicks", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.VarBind(SysDescr, [0x43, 0x00])) },
            { "value length beyond varbind", Ber.Message(1, "public", 0xA2, 1, 0, 0, Ber.Tlv(0x30, Ber.Oid(SysDescr), [0x04, 0x05, 0x41])) },
        };
    }

    [Fact]
    public void Rejects_every_truncation_of_a_valid_message()
    {
        byte[] valid = ValidResponse();

        for (int length = 0; length < valid.Length; length++)
        {
            byte[] truncated = valid[..length];
            Assert.Throws<FormatException>(() => SnmpCodec.Decode(truncated));
        }
    }

    [Fact]
    public void Random_corruption_never_escapes_as_anything_but_FormatException()
    {
        byte[] valid = ValidResponse();
        var random = new Random(20261103);

        for (int round = 0; round < 5000; round++)
        {
            List<byte> mutated = [.. valid];
            int edits = random.Next(1, 4);
            for (int i = 0; i < edits; i++)
            {
                int position = random.Next(mutated.Count);
                switch (random.Next(3))
                {
                    case 0:
                        mutated[position] = (byte)random.Next(256);
                        break;
                    case 1:
                        mutated.Insert(position, (byte)random.Next(256));
                        break;
                    default:
                        if (mutated.Count > 1)
                            mutated.RemoveAt(position);
                        break;
                }
            }

            byte[] input = [.. mutated];
            try
            {
                SnmpCodec.Decode(input);
            }
            catch (FormatException)
            {
                // Expected for most corruptions.
            }
            catch (Exception ex)
            {
                Assert.Fail($"Round {round}: {ex.GetType().Name} for {Convert.ToHexString(input)}: {ex.Message}");
            }
        }
    }

    [Theory]
    [InlineData(0, "noError")]
    [InlineData(1, "tooBig")]
    [InlineData(2, "noSuchName")]
    [InlineData(18, "inconsistentName")]
    [InlineData(19, "error-status 19")]
    public void Names_error_statuses(int status, string expected)
    {
        Assert.Equal(expected, SnmpCodec.DescribeError(status));
    }

    private static SnmpValue DecodeSingleValue(byte[] valueTlv) =>
        Assert.Single(SnmpCodec.Decode(Ber.Response(1, 1, Ber.VarBind(SysDescr, valueTlv))).VarBinds).Value;

    private static byte[] ValidResponse() =>
        Ber.Response(1, 99,
            Ber.VarBind(SysDescr, Ber.OctetString("KYOCERA")),
            Ber.VarBind(SysObjectId, Ber.Oid("1.3.6.1.4.1.1347.41")),
            Ber.VarBind("1.3.6.1.2.1.1.3.0", Ber.Tlv(0x43, [0x01, 0x02, 0x03])),
            Ber.VarBind("1.3.6.1.2.1.43.5.1.1.16.1", Ber.NoSuchInstance));
}
