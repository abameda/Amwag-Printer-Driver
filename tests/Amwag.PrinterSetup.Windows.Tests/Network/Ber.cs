using System.Globalization;
using System.Text;

namespace Amwag.PrinterSetup.Windows.Tests.Network;

/// <summary>
/// A deliberately separate, minimal BER writer for crafting SNMP messages in tests, so decoder tests do not depend on the
/// production encoder.
/// </summary>
internal static class Ber
{
    public static byte[] Null => [0x05, 0x00];
    public static byte[] NoSuchObject => [0x80, 0x00];
    public static byte[] NoSuchInstance => [0x81, 0x00];
    public static byte[] EndOfMibView => [0x82, 0x00];

    public static byte[] Tlv(byte tag, params byte[][] contents)
    {
        byte[] content = contents.SelectMany(c => c).ToArray();
        var header = new List<byte> { tag };
        if (content.Length < 0x80)
        {
            header.Add((byte)content.Length);
        }
        else
        {
            byte[] lengthOctets = BitConverter.GetBytes(content.Length).Reverse().SkipWhile(b => b == 0).ToArray();
            header.Add((byte)(0x80 | lengthOctets.Length));
            header.AddRange(lengthOctets);
        }

        return [.. header, .. content];
    }

    public static byte[] Integer(long value)
    {
        var octets = new List<byte>(BitConverter.GetBytes(value).Reverse());
        while (octets.Count > 1 && ((octets[0] == 0x00 && octets[1] < 0x80) || (octets[0] == 0xFF && octets[1] >= 0x80)))
            octets.RemoveAt(0);
        return Tlv(0x02, [.. octets]);
    }

    public static byte[] OctetString(string text) => Tlv(0x04, Encoding.UTF8.GetBytes(text));

    public static byte[] OctetString(byte[] raw) => Tlv(0x04, raw);

    public static byte[] Oid(string dotted)
    {
        ulong[] arcs = dotted.Split('.').Select(a => ulong.Parse(a, CultureInfo.InvariantCulture)).ToArray();
        var content = new List<byte>();
        foreach (ulong subIdentifier in arcs.Skip(2).Prepend(arcs[0] * 40 + arcs[1]))
        {
            var groups = new Stack<byte>();
            ulong rest = subIdentifier;
            do
            {
                groups.Push((byte)(rest & 0x7F));
                rest >>= 7;
            }
            while (rest != 0);

            while (groups.Count > 0)
                content.Add((byte)(groups.Pop() | (groups.Count > 0 ? 0x80 : 0)));
        }

        return Tlv(0x06, [.. content]);
    }

    public static byte[] VarBind(string oid, byte[] value) => Tlv(0x30, Oid(oid), value);

    public static byte[] Message(int version, string community, byte pduTag, int requestId, int errorStatus, int errorIndex, params byte[][] varBinds) =>
        Tlv(0x30,
            Integer(version),
            OctetString(community),
            Tlv(pduTag, Integer(requestId), Integer(errorStatus), Integer(errorIndex), Tlv(0x30, varBinds)));

    public static byte[] Response(int version, int requestId, params byte[][] varBinds) =>
        Message(version, "public", 0xA2, requestId, 0, 0, varBinds);
}
