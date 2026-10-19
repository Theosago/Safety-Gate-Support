using System.Text;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport.SiemensValidation;

internal static class WireChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases
    {
        get
        {
            yield return ("wire: exact read-only information request", () => EqualBytes(
                Ascii("GET /api/v1/info HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"),
                SiemensWire.GetInfoRequest()));
            yield return ("wire: exact read-only configuration request", () => EqualBytes(
                Ascii("GET /api/v1/config HTTP/1.0\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"),
                SiemensWire.GetConfigRequest()));
            yield return ("wire: requests are independent immutable templates", () =>
            {
                byte[] first = SiemensWire.GetInfoRequest();
                first[0] = 0;
                Check(SiemensWire.GetInfoRequest()[0] == 'G', "Request template was mutable.");
            });
            yield return ("wire: binary body survives every fragmentation width", () =>
            {
                byte[] body = [0, 255, 13, 10, 0x50, 0x4b, 0x80, 0xC0, 0xFF];
                byte[] header = Ascii($"HTTP/1.0 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\n\r\n");
                for (int width = 1; width <= header.Length + body.Length; width++)
                {
                    var response = Read(header.Concat(body).ToArray(), width);
                    Check(response.StatusCode == 200 && response.ReasonPhrase == "OK", "Incorrect status.");
                    EqualBytes(body, response.Body);
                    Check(response.Headers["content-type"] == "application/octet-stream", "Header lookup must ignore case.");
                }
            });
            yield return ("wire: JSON UTF-8 body is opaque", () =>
            {
                byte[] body = Encoding.UTF8.GetBytes("{\"name\":\"é\"}");
                EqualBytes(body, Read(Ascii($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n").Concat(body).ToArray()).Body);
            });
            yield return ("wire: empty explicit-length response", () =>
                Check(Read("HTTP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n").Body.Length == 0, "Body was not empty."));
            yield return ("wire: non-success status preserved", () =>
            {
                var response = Read("HTTP/1.1 422 Unprocessable Content\r\nContent-Length: 3\r\n\r\nbad");
                Check(response.StatusCode == 422 && response.ReasonPhrase == "Unprocessable Content", "Error status was lost.");
                EqualBytes(Ascii("bad"), response.Body);
            });
            yield return ("wire: status can have an empty reason phrase", () =>
                Check(Read("HTTP/1.1 200\r\nContent-Length: 0\r\n\r\n").ReasonPhrase == "", "Empty reason was not preserved."));
            yield return ("wire: header values trim optional whitespace", () =>
                Check(Read("HTTP/1.1 200 OK\r\ncOnTeNt-LeNgTh:\t 1 \t\r\n\r\nx").Body[0] == 'x', "OWS not handled."));
            yield return ("wire: ordinary repeated headers combine", () =>
                Check(Read("HTTP/1.1 200 OK\r\nX-Test: a\r\nx-test: b\r\nContent-Length: 0\r\n\r\n").Headers["X-Test"] == "a, b", "Repeated value missing."));
            yield return ("wire: chunked binary response and extension", () =>
            {
                byte[] message = Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: CHUNKED\r\n\r\n2;test=yes\r\n")
                    .Concat(new byte[] { 0, 255 }).Concat(Ascii("\r\n3\r\nabc\r\n0\r\nX-Checksum: fixture\r\n\r\n")).ToArray();
                for (int width = 1; width <= 17; width++)
                {
                    var response = Read(message, width);
                    EqualBytes([0, 255, (byte)'a', (byte)'b', (byte)'c'], response.Body);
                    Check(!response.Headers.ContainsKey("X-Checksum"), "Trailer overwrote initial headers.");
                }
            });
            yield return ("wire: chunked empty body", () =>
                Check(Read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n").Body.Length == 0, "Chunked body not empty."));
            yield return ("wire: chunk extensions support quoted values and escapes", () =>
                EqualBytes(Ascii("x"), Read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1 \t;name=\"a;b\\\"c\";flag; spaces = token \r\nx\r\n0\r\n\r\n").Body));
            yield return ("wire: multiple interim responses followed by final response", () =>
            {
                var response = Read("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 103 Early Hints\r\nLink: fixture\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nx", 1);
                Check(response.StatusCode == 200 && response.Body[0] == 'x', "Interim response was returned as final.");
            });
            yield return ("wire: 204 needs no length", () =>
                Check(Read("HTTP/1.1 204 No Content\r\n\r\n").Body.Length == 0, "204 read a body."));
            yield return ("wire: 204 tolerates zero length", () =>
                Check(Read("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n").StatusCode == 204, "204 rejected."));
            yield return ("wire: 304 length metadata does not cause a body read", () =>
                Check(Read("HTTP/1.1 304 Not Modified\r\nContent-Length: 12345\r\n\r\n").Body.Length == 0, "304 read representation metadata as body."));
            yield return ("wire: final response does not consume subsequent bytes", () =>
            {
                using var stream = new FragmentedStream(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\nxNEXT"), 1000);
                SiemensWire.ReadResponse(stream);
                Check(stream.ReadByte() == 'N', "Parser consumed bytes from the next message.");
            });
            yield return ("wire: chunked final response leaves subsequent bytes untouched", () =>
            {
                using var stream = new FragmentedStream(Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\nx\r\n0\r\n\r\nNEXT"), 1000);
                SiemensWire.ReadResponse(stream);
                Check(stream.ReadByte() == 'N', "Chunk parser consumed the next message.");
            });
            yield return ("wire: body exactly at configured limit", () =>
                EqualBytes(Ascii("abcd"), Read("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nabcd", maxBody: 4).Body));
            yield return ("wire: zero body limit supports empty replies", () =>
                Check(Read("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", maxBody: 0).Body.Length == 0, "Zero body limit failed."));

            foreach (var (name, message) in InvalidFixtures)
                yield return ($"wire: rejects {name}", () => Throws<InvalidDataException>(() => Read(message)));

            yield return ("wire: rejects content length above limit before reading", () =>
                Throws<InvalidDataException>(() => Read("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\n", maxBody: 4)));
            yield return ("wire: rejects actual non-ASCII header octets", () =>
                Throws<InvalidDataException>(() => Read(Ascii("HTTP/1.1 200 OK\r\nX-Test: ").Concat(new byte[] { 128 }).Concat(Ascii("\r\n\r\n")).ToArray())));
            yield return ("wire: rejects cumulative chunks above limit", () =>
                Throws<InvalidDataException>(() => Read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2\r\n", maxBody: 4)));
            yield return ("wire: rejects oversized single header line", () =>
                Throws<InvalidDataException>(() => Read("HTTP/1.1 200 OK\r\nX-Large: " + new string('a', 8192) + "\r\n\r\n")));
            yield return ("wire: rejects excessive total headers", () =>
                Throws<InvalidDataException>(() => Read("HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Repeat("X-Large: " + new string('a', 4000) + "\r\n", 17)) + "\r\n")));
            yield return ("wire: rejects excessive interim replies", () =>
                Throws<InvalidDataException>(() => Read(string.Concat(Enumerable.Repeat("HTTP/1.1 100 Continue\r\n\r\n", 9)))));
            yield return ("wire: rejects EOF at each truncated message position", () =>
            {
                byte[] complete = Ascii("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc");
                for (int length = 0; length < complete.Length; length++)
                    Throws<EndOfStreamException>(() => Read(complete[..length], 1));
            });
            yield return ("wire: rejects truncated chunk stream", () =>
                Throws<EndOfStreamException>(() => Read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nab")));
            yield return ("wire: cancellation before reading", () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                using var stream = new MemoryStream();
                Throws<OperationCanceledException>(() => SiemensWire.ReadResponse(stream, cancellation.Token));
                Check(stream.Position == 0, "Canceled operation read input.");
            });
            yield return ("wire: cancellation during fragmented reads", () =>
            {
                using var cancellation = new CancellationTokenSource();
                using var stream = new CancelingStream(Ascii("HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc"), cancellation);
                Throws<OperationCanceledException>(() => SiemensWire.ReadResponse(stream, cancellation.Token));
            });
            yield return ("wire: rejects negative body limit", () =>
                Throws<ArgumentOutOfRangeException>(() => Read("", maxBody: -1)));
            yield return ("wire: rejects null stream", () =>
                Throws<ArgumentNullException>(() => SiemensWire.ReadResponse(null!)));
        }
    }

    private static IEnumerable<(string Name, string Message)> InvalidFixtures =>
    [
        ("HTTP/2 status", "HTTP/2.0 200 OK\r\nContent-Length: 0\r\n\r\n"),
        ("non-numeric status", "HTTP/1.1 2a0 OK\r\nContent-Length: 0\r\n\r\n"),
        ("unsupported status range", "HTTP/1.1 999 Strange\r\nContent-Length: 0\r\n\r\n"),
        ("missing status delimiter", "HTTP/1.1 200OK\r\nContent-Length: 0\r\n\r\n"),
        ("bare LF", "HTTP/1.1 200 OK\nContent-Length: 0\n\n"),
        ("bare CR", "HTTP/1.1 200 OK\rX"),
        ("non-ASCII header", "HTTP/1.1 200 OK\r\nX-Test: \u007f\r\n\r\n"),
        ("header control byte", "HTTP/1.1 200 OK\r\nX-Test: \0\r\n\r\n"),
        ("folded headers", "HTTP/1.1 200 OK\r\n Content-Length: 0\r\n\r\n"),
        ("whitespace before header colon", "HTTP/1.1 200 OK\r\nContent-Length : 0\r\n\r\n"),
        ("header without colon", "HTTP/1.1 200 OK\r\nContent-Length 0\r\n\r\n"),
        ("empty header name", "HTTP/1.1 200 OK\r\n: value\r\n\r\n"),
        ("duplicate identical content lengths", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\ncontent-length: 0\r\n\r\n"),
        ("conflicting content lengths", "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n"),
        ("duplicate transfer encodings", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n"),
        ("ambiguous chunked plus length", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 0\r\n\r\n"),
        ("missing body framing", "HTTP/1.1 200 OK\r\n\r\n"),
        ("negative content length", "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n"),
        ("positive sign in content length", "HTTP/1.1 200 OK\r\nContent-Length: +1\r\n\r\n"),
        ("comma separated content lengths", "HTTP/1.1 200 OK\r\nContent-Length: 0,0\r\n\r\n"),
        ("empty content length", "HTTP/1.1 200 OK\r\nContent-Length:\r\n\r\n"),
        ("overflowing content length", "HTTP/1.1 200 OK\r\nContent-Length: 18446744073709551616\r\n\r\n"),
        ("huge declared body", "HTTP/1.1 200 OK\r\nContent-Length: 2147483647\r\n\r\n"),
        ("unsupported transfer encoding", "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n"),
        ("protocol upgrade", "HTTP/1.1 101 Switching Protocols\r\n\r\n"),
        ("interim response with body", "HTTP/1.1 100 Continue\r\nContent-Length: 1\r\n\r\nx"),
        ("204 with body", "HTTP/1.1 204 No Content\r\nContent-Length: 1\r\n\r\nx"),
        ("204 chunked framing", "HTTP/1.1 204 No Content\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"),
        ("invalid chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nz\r\n"),
        ("negative chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n-1\r\n"),
        ("empty chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n;extension\r\n"),
        ("empty chunk extension name", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;\r\n"),
        ("missing chunk extension value", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;test=\r\n"),
        ("unterminated quoted chunk extension", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;test=\"neverclosed\r\n"),
        ("incomplete chunk extension escape", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;test=\"slash\\\r\n"),
        ("garbage after chunk extension value", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;test=\"value\"xyz\r\n"),
        ("overflowing chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n10000000000000000\r\n"),
        ("huge chunk", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n7fffffff\r\n"),
        ("missing CRLF after chunk", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1\r\nx!\n"),
        ("framing redefinition in trailers", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nContent-Length: 0\r\n\r\n"),
        ("transfer redefinition in trailers", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nTransfer-Encoding: chunked\r\n\r\n"),
        ("connection redefinition in trailers", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nConnection: close\r\n\r\n")
    ];

    private static SiemensHttpResponse Read(string text, int width = 3, int maxBody = 16 * 1024 * 1024) =>
        Read(Ascii(text), width, maxBody);

    private static SiemensHttpResponse Read(byte[] data, int width = 3, int maxBody = 16 * 1024 * 1024)
    {
        using var stream = new FragmentedStream(data, width);
        return SiemensWire.ReadResponse(stream, maxBodyBytes: maxBody);
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static void EqualBytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Byte sequences differ.");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FragmentedStream(byte[] data, int width) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(width, count));
    }

    private sealed class CancelingStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data, writable: false)
    {
        private int reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int result = base.Read(buffer, offset, Math.Min(1, count));
            if (++reads == 5) cancellation.Cancel();
            return result;
        }
    }
}
