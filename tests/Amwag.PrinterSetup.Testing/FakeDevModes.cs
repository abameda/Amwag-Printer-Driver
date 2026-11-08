using System.Buffers.Binary;
using System.Text;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.Testing;

/// <summary>Builds realistic DEVMODEW buffers: a 220-byte public part (dmSpecVersion 0x0401) followed by driver-private bytes.</summary>
public static class FakeDevModes
{
    public const ushort SpecVersion = 0x0401;

    public const DevModeFields StandardFields =
        DevModeFields.Orientation | DevModeFields.PaperSize | DevModeFields.Scale | DevModeFields.Copies |
        DevModeFields.DefaultSource | DevModeFields.PrintQuality | DevModeFields.Color | DevModeFields.Duplex |
        DevModeFields.YResolution | DevModeFields.TTOption | DevModeFields.Collate | DevModeFields.FormName |
        DevModeFields.Nup | DevModeFields.MediaType;

    private const int OffsetSpecVersion = 64;
    private const int OffsetDriverVersion = 66;
    private const int OffsetSize = 68;
    private const int OffsetDriverExtra = 70;
    private const int OffsetNup = 180;
    private const int OffsetMediaType = 196;
    private const int DeviceNameChars = 32;

    public static DevMode Create(
        string deviceName,
        short paperSize,
        string formName,
        short orientation = DmOrientation.Portrait,
        short duplex = DmDuplex.Simplex,
        short scale = 100,
        short defaultSource = DmBin.Auto,
        int privateSize = 512,
        ushort driverVersion = 0x0600)
    {
        var buffer = new byte[DevMode.CurrentPublicSize + privateSize];
        WriteDeviceName(buffer, deviceName);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(OffsetSpecVersion), SpecVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(OffsetDriverVersion), driverVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(OffsetSize), DevMode.CurrentPublicSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(OffsetDriverExtra), checked((ushort)privateSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetNup), 1);       // DMNUP_SYSTEM
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetMediaType), 1); // DMMEDIA_STANDARD
        PrivateBytes(privateSize).CopyTo(buffer.AsSpan(DevMode.CurrentPublicSize));

        var devMode = DevMode.FromBytes(buffer);
        devMode.Fields = StandardFields;
        devMode.Orientation = orientation;
        devMode.PaperSize = paperSize;
        devMode.FormName = formName;
        devMode.Scale = scale;
        devMode.Copies = 1;
        devMode.DefaultSource = defaultSource;
        devMode.PrintQuality = 600;
        devMode.Color = 1;
        devMode.Duplex = duplex;
        devMode.YResolution = 600;
        devMode.TTOption = 1;
        devMode.Collate = 0;
        return devMode;
    }

    /// <summary>The DEVMODE a queue gets when it is created with a driver of this profile.</summary>
    public static DevMode CreateDefault(FakeDriverProfile profile, string deviceName)
    {
        var paper = profile.DefaultPaper;
        return Create(deviceName, paper.Id, paper.Name, profile.DefaultOrientation, profile.DefaultDuplex, profile.DefaultScale,
            profile.DefaultSource, profile.PrivateSize, profile.DevModeDriverVersion);
    }

    /// <summary>Deterministic, non-trivial driver-private bytes ("KXDM" signature followed by a pattern).</summary>
    public static byte[] PrivateBytes(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)((i * 31 + 7) & 0xFF);
        Encoding.ASCII.GetBytes("KXDM").AsSpan(0, Math.Min(4, size)).CopyTo(bytes);
        return bytes;
    }

    private static void WriteDeviceName(byte[] buffer, string deviceName)
    {
        string name = deviceName.Length < DeviceNameChars ? deviceName : deviceName[..(DeviceNameChars - 1)];
        Encoding.Unicode.GetBytes(name, buffer.AsSpan(0, DeviceNameChars * 2));
    }
}
