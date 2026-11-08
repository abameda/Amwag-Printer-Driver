using System.Buffers.Binary;
using System.Text;

namespace Amwag.PrinterSetup.Core.Printing;

/// <summary>
/// A DEVMODEW buffer (public part followed by dmDriverExtra driver-private bytes). Only public fields are edited;
/// driver-private bytes are preserved untouched so DocumentProperties can merge them. Offsets follow wingdi.h (x86/x64 identical).
/// </summary>
public sealed class DevMode
{
    /// <summary>sizeof(DEVMODEW) on Windows 2000 and later.</summary>
    public const int CurrentPublicSize = 220;

    /// <summary>Smallest public size that still contains dmFormName.</summary>
    public const int MinimumPublicSize = OffsetFormName + NameBytes;

    /// <summary>Longest form name (characters) that fits dmFormName with its terminating NUL.</summary>
    public const int MaxNameLength = NameChars - 1;

    private const int NameChars = 32;
    private const int NameBytes = NameChars * 2;

    private const int OffsetDeviceName = 0;
    private const int OffsetSpecVersion = 64;
    private const int OffsetDriverVersion = 66;
    private const int OffsetSize = 68;
    private const int OffsetDriverExtra = 70;
    private const int OffsetFields = 72;
    private const int OffsetOrientation = 76;
    private const int OffsetPaperSize = 78;
    private const int OffsetPaperLength = 80;
    private const int OffsetPaperWidth = 82;
    private const int OffsetScale = 84;
    private const int OffsetCopies = 86;
    private const int OffsetDefaultSource = 88;
    private const int OffsetPrintQuality = 90;
    private const int OffsetColor = 92;
    private const int OffsetDuplex = 94;
    private const int OffsetYResolution = 96;
    private const int OffsetTTOption = 98;
    private const int OffsetCollate = 100;
    private const int OffsetFormName = 102;
    private const int OffsetNup = 180;
    private const int OffsetMediaType = 196;

    private readonly byte[] _buffer;

    private DevMode(byte[] buffer) => _buffer = buffer;

    /// <summary>Copies a DEVMODEW from a buffer, validating dmSize and dmDriverExtra against its length.</summary>
    public static DevMode FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinimumPublicSize)
            throw new ArgumentException($"A DEVMODEW needs at least {MinimumPublicSize} bytes; got {bytes.Length}.", nameof(bytes));

        int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes[OffsetSize..]);
        int extra = BinaryPrimitives.ReadUInt16LittleEndian(bytes[OffsetDriverExtra..]);
        if (size < MinimumPublicSize)
            throw new ArgumentException($"dmSize {size} is smaller than {MinimumPublicSize}.", nameof(bytes));
        if (bytes.Length < size + extra)
            throw new ArgumentException($"Buffer of {bytes.Length} bytes is shorter than dmSize + dmDriverExtra ({size} + {extra}).", nameof(bytes));

        return new DevMode(bytes[..(size + extra)].ToArray());
    }

    /// <summary>Total byte length (dmSize + dmDriverExtra).</summary>
    public int TotalSize => _buffer.Length;

    public byte[] ToArray() => (byte[])_buffer.Clone();

    public ReadOnlySpan<byte> AsSpan() => _buffer;

    public DevMode Clone() => new((byte[])_buffer.Clone());

    public string DeviceName => ReadString(OffsetDeviceName);
    public ushort SpecVersion => ReadUInt16(OffsetSpecVersion);
    public ushort DriverVersion => ReadUInt16(OffsetDriverVersion);
    public ushort Size => ReadUInt16(OffsetSize);
    public ushort DriverExtra => ReadUInt16(OffsetDriverExtra);

    public DevModeFields Fields
    {
        get => (DevModeFields)ReadUInt32(OffsetFields);
        set => WriteUInt32(OffsetFields, (uint)value);
    }

    public bool Has(DevModeFields field) => (Fields & field) == field;

    public short Orientation { get => ReadInt16(OffsetOrientation); set => WriteInt16(OffsetOrientation, value); }
    public short PaperSize { get => ReadInt16(OffsetPaperSize); set => WriteInt16(OffsetPaperSize, value); }
    /// <summary>dmPaperLength in tenths of a millimeter.</summary>
    public short PaperLength { get => ReadInt16(OffsetPaperLength); set => WriteInt16(OffsetPaperLength, value); }
    /// <summary>dmPaperWidth in tenths of a millimeter.</summary>
    public short PaperWidth { get => ReadInt16(OffsetPaperWidth); set => WriteInt16(OffsetPaperWidth, value); }
    public short Scale { get => ReadInt16(OffsetScale); set => WriteInt16(OffsetScale, value); }
    public short Copies { get => ReadInt16(OffsetCopies); set => WriteInt16(OffsetCopies, value); }
    public short DefaultSource { get => ReadInt16(OffsetDefaultSource); set => WriteInt16(OffsetDefaultSource, value); }
    public short PrintQuality { get => ReadInt16(OffsetPrintQuality); set => WriteInt16(OffsetPrintQuality, value); }
    public short Color { get => ReadInt16(OffsetColor); set => WriteInt16(OffsetColor, value); }
    public short Duplex { get => ReadInt16(OffsetDuplex); set => WriteInt16(OffsetDuplex, value); }
    public short YResolution { get => ReadInt16(OffsetYResolution); set => WriteInt16(OffsetYResolution, value); }
    public short TTOption { get => ReadInt16(OffsetTTOption); set => WriteInt16(OffsetTTOption, value); }
    public short Collate { get => ReadInt16(OffsetCollate); set => WriteInt16(OffsetCollate, value); }

    /// <summary>dmFormName; setting a name longer than <see cref="MaxNameLength"/> characters throws.</summary>
    public string FormName
    {
        get => ReadString(OffsetFormName);
        set => WriteString(OffsetFormName, value);
    }

    /// <summary>dmNup, when the public part is large enough to contain it.</summary>
    public uint? Nup => Size >= OffsetNup + 4 ? ReadUInt32(OffsetNup) : null;

    /// <summary>dmMediaType, when the public part is large enough to contain it.</summary>
    public uint? MediaType => Size >= OffsetMediaType + 4 ? ReadUInt32(OffsetMediaType) : null;

    private short ReadInt16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(offset));
    private ushort ReadUInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(offset));
    private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(offset));
    private void WriteInt16(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(offset), value);
    private void WriteUInt32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset), value);

    private string ReadString(int offset)
    {
        var chars = _buffer.AsSpan(offset, NameBytes);
        int length = 0;
        while (length < NameChars && BinaryPrimitives.ReadUInt16LittleEndian(chars[(length * 2)..]) != 0)
            length++;
        return Encoding.Unicode.GetString(chars[..(length * 2)]);
    }

    private void WriteString(int offset, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxNameLength)
            throw new ArgumentException($"'{value}' is longer than {MaxNameLength} characters.", nameof(value));

        var target = _buffer.AsSpan(offset, NameBytes);
        target.Clear();
        Encoding.Unicode.GetBytes(value, target);
    }
}
