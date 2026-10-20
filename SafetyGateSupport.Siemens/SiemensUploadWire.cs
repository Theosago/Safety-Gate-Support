using System.Globalization;
using System.Text;

namespace SafetyGateSupport.Siemens;

/// <summary>Only the observed configuration upload and normal application restart.</summary>
public static class SiemensUploadWire
{
    public static byte[] ConfigurationRequest(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var header = Encoding.ASCII.GetBytes("PUT /api/v1/config HTTP/1.0\r\nContent-Length: " +
            archive.Length.ToString(CultureInfo.InvariantCulture) +
            "\r\nContent-Type: application/octet-stream\r\nConnection: keep-alive\r\n\r\n");
        var request = new byte[checked(header.Length + archive.Length)];
        header.CopyTo(request, 0);
        archive.CopyTo(request, header.Length);
        return request;
    }

    // The vendor marks this request as not expecting a response. Completion of
    // the write alone is not proof of restart or successful configuration.
    public static byte[] ApplicationRestartRequest() => Encoding.ASCII.GetBytes(
        "PUT /api/v1/system/reset/app HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
}
