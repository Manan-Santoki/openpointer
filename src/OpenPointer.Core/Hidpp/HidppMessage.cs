namespace OpenPointer.Hidpp;

/// <summary>
/// A raw HID++ report. Short reports are 7 bytes (ID 0x10), long reports are 20 bytes (ID 0x11).
/// Layout: [reportId, deviceIndex, featureIndex|subId, function<<4|softwareId|address, params...].
/// </summary>
public readonly struct HidppMessage
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const int ShortLength = 7;
    public const int LongLength = 20;

    /// <summary>Device index of the receiver itself, or of a directly connected (Bluetooth/USB) device.</summary>
    public const byte ReceiverIndex = 0xFF;

    /// <summary>Feature index used by HID++ 2.0 error replies.</summary>
    public const byte Hidpp20ErrorIndex = 0xFF;

    /// <summary>Sub-ID used by HID++ 1.0 error replies.</summary>
    public const byte Hidpp10ErrorSubId = 0x8F;

    private readonly byte[] _data;

    public HidppMessage(byte[] data)
    {
        if (data.Length < 4) throw new ArgumentException("HID++ message is too short.", nameof(data));
        _data = data;
    }

    public byte[] Raw => _data;
    public byte ReportId => _data[0];
    public bool IsLong => _data[0] == LongReportId;
    public byte DeviceIndex => _data[1];
    public byte FeatureIndex => _data[2];
    public byte FunctionAndSoftwareId => _data[3];
    public int Function => _data[3] >> 4;
    public int SoftwareId => _data[3] & 0x0F;
    public ReadOnlySpan<byte> Params => _data.AsSpan(4);

    public static HidppMessage Long(byte deviceIndex, byte featureIndex, int function, int softwareId, ReadOnlySpan<byte> args)
    {
        if (args.Length > LongLength - 4) throw new ArgumentException("Too many parameters for a long report.", nameof(args));
        var data = new byte[LongLength];
        data[0] = LongReportId;
        data[1] = deviceIndex;
        data[2] = featureIndex;
        data[3] = (byte)(((function & 0x0F) << 4) | (softwareId & 0x0F));
        args.CopyTo(data.AsSpan(4));
        return new HidppMessage(data);
    }

    public static HidppMessage Short(byte deviceIndex, byte subId, byte address, ReadOnlySpan<byte> args)
    {
        if (args.Length > ShortLength - 4) throw new ArgumentException("Too many parameters for a short report.", nameof(args));
        var data = new byte[ShortLength];
        data[0] = ShortReportId;
        data[1] = deviceIndex;
        data[2] = subId;
        data[3] = address;
        args.CopyTo(data.AsSpan(4));
        return new HidppMessage(data);
    }

    public override string ToString() => Convert.ToHexString(_data);
}

public sealed class HidppException : Exception
{
    public HidppException(string message, int errorCode = -1) : base(message) => ErrorCode = errorCode;

    public int ErrorCode { get; }
}
