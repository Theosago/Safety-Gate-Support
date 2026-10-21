using System.Globalization;
using System.Text;

namespace SafetyGateSupport.Hokuyo;

public sealed record UamReply(string Command, string Status, string Data);

/// <summary>Bounded ASCII framing. Only fixed version and status read requests are exposed.</summary>
public static class UamWire
{
    public const int MaxFrameLength = ushort.MaxValue;
    public static byte[] IdentityRequest() => Encode("VR42");
    public static byte[] LegacyIdentityRequest() => Encode("VR00");
    public static byte[] StatusRequest() => Encode("AR00");
    private static byte[] Encode(string command)
    {
        string body = "000E" + command;
        return Encoding.ASCII.GetBytes("\x02" + body + Crc(Encoding.ASCII.GetBytes(body)).ToString("X4", CultureInfo.InvariantCulture) + "\x03");
    }
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x8408));
        }
        return crc;
    }
    public static UamReply Decode(byte[] frame, string expectedCommand)
    {
        if (expectedCommand is not ("VR42" or "VR00" or "AR00")) throw new ArgumentException("Unsupported read reply.", nameof(expectedCommand));
        if (frame.Length < 16 || frame.Length > MaxFrameLength || frame[0] != 2 || frame[^1] != 3) throw new InvalidDataException("Incomplete or oversized UAM frame.");
        if (Hex(frame.AsSpan(1, 4)) != frame.Length) throw new InvalidDataException("UAM frame length mismatch.");
        if (Hex(frame.AsSpan(frame.Length - 5, 4)) != Crc(frame.AsSpan(1, frame.Length - 6))) throw new InvalidDataException("UAM reply CRC mismatch.");
        foreach (byte value in frame.AsSpan(1, frame.Length - 2)) if (value < 32 || value > 126) throw new InvalidDataException("Non-ASCII UAM reply.");
        string body = Encoding.ASCII.GetString(frame, 5, frame.Length - 10);
        string replyCommand = body[..4];
        // Native version decoding correlates the VR family (two characters).
        // Permit its standard VR00 reply to a VR42 query; never cross command families.
        if (replyCommand != expectedCommand && !(expectedCommand == "VR42" && replyCommand == "VR00"))
            throw new InvalidDataException("Unexpected UAM reply command; no further requests sent.");
        string status = body.Substring(4, 2);
        _ = Hex(Encoding.ASCII.GetBytes(status));
        return new UamReply(replyCommand, status, body[6..]);
    }
    internal static int Hex(ReadOnlySpan<byte> bytes)
    {
        int result = 0;
        foreach (byte b in bytes)
        {
            int digit = b is >= (byte)'0' and <= (byte)'9' ? b - '0' : b is >= (byte)'A' and <= (byte)'F' ? b - 'A' + 10 : -1;
            if (digit < 0) throw new InvalidDataException("Invalid uppercase hexadecimal UAM field.");
            result = (result << 4) | digit;
        }
        return result;
    }
}
