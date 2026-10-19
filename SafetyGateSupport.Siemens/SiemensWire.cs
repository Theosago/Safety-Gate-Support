using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace SafetyGateSupport.Siemens;

public sealed record SiemensHttpResponse(
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

/// <summary>
/// Read-only HTTP framing used by the 8WD46 virtual serial port. This is not an
/// HTTP network client: callers supply the already-open, correctly configured stream.
/// No configuration, reset, firmware, or arbitrary request writer is exposed.
/// </summary>
public static class SiemensWire
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxLineBytes = 8 * 1024;
    private const int MaxInterimResponses = 8;

    public static byte[] GetInfoRequest() => Encoding.ASCII.GetBytes(
        "GET /api/v1/info HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

    public static byte[] GetConfigRequest() => Encoding.ASCII.GetBytes(
        "GET /api/v1/config HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");

    /// <summary>
    /// Reads exactly one final response, leaving subsequent bytes untouched. Supports
    /// Content-Length and chunked bodies; close-delimited bodies and protocol upgrades
    /// are intentionally unsupported. Non-2xx responses are returned for caller review.
    /// Stream timeout policy belongs to the caller. Cancellation is checked between
    /// synchronous reads, so the stream must also provide a finite read timeout.
    /// </summary>
    public static SiemensHttpResponse ReadResponse(
        Stream input,
        CancellationToken cancellationToken = default,
        int maxBodyBytes = 16 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBodyBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new ResponseReader(input, cancellationToken);

        for (int interimCount = 0; ; interimCount++)
        {
            var (statusCode, reasonPhrase) = ParseStatus(reader.ReadHeaderLine());
            var headers = ReadHeaders(reader);
            bool hasLength = headers.TryGetValue("Content-Length", out var lengthValue);
            bool hasTransfer = headers.TryGetValue("Transfer-Encoding", out var transferValue);
            if (hasLength && hasTransfer)
                throw Invalid("Response includes both Content-Length and Transfer-Encoding.");

            ulong length = hasLength ? ParseLength(lengthValue!) : 0;
            if (hasTransfer && !string.Equals(transferValue, "chunked", StringComparison.OrdinalIgnoreCase))
                throw Invalid("Only an unambiguous chunked transfer encoding is supported.");

            if (statusCode < 200)
            {
                if (statusCode == 101)
                    throw Invalid("Protocol upgrades are unsupported.");
                if (hasTransfer || (hasLength && length != 0))
                    throw Invalid("An informational response cannot contain a body.");
                if (interimCount >= MaxInterimResponses)
                    throw Invalid("Too many informational responses.");
                continue;
            }

            byte[] body;
            if (statusCode is 204 or 304)
            {
                // 304 Content-Length can describe the selected representation, not a
                // response body. A 204 is tolerated with an explicit zero length.
                if (hasTransfer || (statusCode == 204 && hasLength && length != 0))
                    throw Invalid("A bodyless response has invalid body framing.");
                body = [];
            }
            else if (hasTransfer)
            {
                body = ReadChunked(reader, maxBodyBytes);
            }
            else if (hasLength)
            {
                if (length > (ulong)maxBodyBytes)
                    throw Invalid("Response body exceeds the configured size limit.");
                body = new byte[(int)length];
                reader.ReadExactly(body, 0, body.Length);
            }
            else
            {
                throw Invalid("A response body needs Content-Length or chunked framing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SiemensHttpResponse(statusCode, reasonPhrase,
                new ReadOnlyDictionary<string, string>(headers), body);
        }
    }

    private static (int Code, string Reason) ParseStatus(string line)
    {
        if (line.Length < 12 ||
            !(line.StartsWith("HTTP/1.0 ", StringComparison.Ordinal) ||
              line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal)) ||
            !char.IsAsciiDigit(line[9]) || !char.IsAsciiDigit(line[10]) || !char.IsAsciiDigit(line[11]) ||
            (line.Length > 12 && line[12] != ' '))
            throw Invalid("Malformed HTTP response status line.");
        int code = int.Parse(line.AsSpan(9, 3), CultureInfo.InvariantCulture);
        if (code < 100 || code > 599)
            throw Invalid("HTTP status is outside the supported range.");
        return (code, line.Length > 12 ? line[13..] : "");
    }

    private static Dictionary<string, string> ReadHeaders(ResponseReader reader, bool trailers = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (;;)
        {
            string line = reader.ReadHeaderLine();
            if (line.Length == 0)
                return headers;
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).ContainsOnlyHeaderNameCharacters())
                throw Invalid("Malformed HTTP header name or obsolete header folding.");
            string name = line[..colon];
            string value = line[(colon + 1)..].Trim(' ', '\t');
            bool framing = name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
            if (trailers && (framing || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
                throw Invalid("Trailers cannot redefine response framing.");
            if (headers.TryGetValue(name, out var oldValue))
            {
                if (framing)
                    throw Invalid("Duplicate response framing headers are unsupported.");
                headers[name] = oldValue + ", " + value;
            }
            else
            {
                headers.Add(name, value);
            }
        }
    }

    private static bool ContainsOnlyHeaderNameCharacters(this ReadOnlySpan<char> name)
    {
        foreach (char c in name)
            if (!IsTokenCharacter(c))
                return false;
        return true;
    }

    private static bool IsTokenCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;

    private static ulong ParseLength(string value)
    {
        if (value.Length == 0 || value.Any(c => !char.IsAsciiDigit(c)) ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result))
            throw Invalid("Malformed or overflowing Content-Length.");
        return result;
    }

    private static byte[] ReadChunked(ResponseReader reader, int maxBodyBytes)
    {
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        for (;;)
        {
            string line = reader.ReadHeaderLine();
            int extension = line.IndexOf(';');
            string number = extension < 0 ? line : line[..extension].TrimEnd(' ', '\t');
            if (number.Length == 0 || number.Any(c => !char.IsAsciiHexDigit(c)) ||
                !ulong.TryParse(number, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong size))
                throw Invalid("Malformed or overflowing chunk size.");
            if (extension >= 0)
                ValidateChunkExtensions(line.AsSpan(extension));
            if (size == 0)
            {
                // Trailer fields are validated and deliberately kept separate from
                // initial response headers; no trailer can replace a device header.
                ReadHeaders(reader, trailers: true);
                return body.ToArray();
            }
            if (size > (ulong)(maxBodyBytes - body.Length))
                throw Invalid("Chunked body exceeds the configured size limit.");
            int remaining = (int)size;
            while (remaining > 0)
            {
                int count = Math.Min(remaining, buffer.Length);
                reader.ReadExactly(buffer, 0, count);
                body.Write(buffer, 0, count);
                remaining -= count;
            }
            if (reader.ReadByte() != '\r' || reader.ReadByte() != '\n')
                throw Invalid("A chunk must end with CRLF.");
        }
    }

    private static void ValidateChunkExtensions(ReadOnlySpan<char> extensions)
    {
        int position = 0;
        while (position < extensions.Length)
        {
            if (extensions[position++] != ';')
                throw Invalid("Malformed chunk extension separator.");
            SkipWhitespace(extensions, ref position);
            int start = position;
            while (position < extensions.Length && IsTokenCharacter(extensions[position])) position++;
            if (position == start)
                throw Invalid("A chunk extension needs a name.");
            SkipWhitespace(extensions, ref position);
            if (position < extensions.Length && extensions[position] == '=')
            {
                position++;
                SkipWhitespace(extensions, ref position);
                if (position < extensions.Length && extensions[position] == '"')
                {
                    position++;
                    bool closed = false;
                    while (position < extensions.Length)
                    {
                        char c = extensions[position++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\')
                        {
                            if (position >= extensions.Length)
                                throw Invalid("Incomplete quoted chunk extension escape.");
                            position++;
                        }
                    }
                    if (!closed)
                        throw Invalid("Unterminated quoted chunk extension.");
                }
                else
                {
                    start = position;
                    while (position < extensions.Length && IsTokenCharacter(extensions[position])) position++;
                    if (position == start)
                        throw Invalid("A chunk extension needs a value after '='.");
                }
                SkipWhitespace(extensions, ref position);
            }
            if (position < extensions.Length && extensions[position] != ';')
                throw Invalid("Malformed chunk extension.");
        }

    }

    private static void SkipWhitespace(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t') position++;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class ResponseReader(Stream input, CancellationToken cancellationToken)
    {
        private readonly byte[] singleByte = new byte[1];
        private int headerBytes;

        public int ReadByte()
        {
            ReadExactly(singleByte, 0, 1);
            return singleByte[0];
        }

        public void ReadExactly(byte[] bytes, int offset, int count)
        {
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int received = input.Read(bytes, offset, count);
                cancellationToken.ThrowIfCancellationRequested();
                if (received == 0)
                    throw new EndOfStreamException("The response ended before its framing was complete.");
                offset += received;
                count -= received;
            }
        }

        public string ReadHeaderLine()
        {
            var text = new StringBuilder();
            for (;;)
            {
                int value = ReadHeaderByte();
                if (value == '\r')
                {
                    if (ReadHeaderByte() != '\n')
                        throw Invalid("HTTP lines must end with CRLF.");
                    return text.ToString();
                }
                if (value != '\t' && (value < 32 || value > 126))
                    throw Invalid("HTTP headers must contain ASCII text and CRLF line endings.");
                if (text.Length >= MaxLineBytes)
                    throw Invalid("HTTP header line exceeds the size limit.");
                text.Append((char)value);
            }
        }

        private int ReadHeaderByte()
        {
            if (++headerBytes > MaxHeaderBytes)
                throw Invalid("HTTP headers exceed the total size limit.");
            return ReadByte();
        }
    }
}
