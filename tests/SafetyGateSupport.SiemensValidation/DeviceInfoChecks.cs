using System.Text;
using SafetyGateSupport.Siemens;

namespace SafetyGateSupport.SiemensValidation;

internal static class DeviceInfoChecks
{
    public static IEnumerable<(string Name, Action Check)> Cases
    {
        get
        {
            yield return ("device: exact target identity", () => Check(Parse("8WD4613-5JH37").IsSupportedTestColumn));
            yield return ("device: legacy target identity", () => Check(Parse("657.101.55").IsSupportedTestColumn));
            yield return ("device: compact legacy target string", () => Check(Parse("65710155").IsSupportedTestColumn));
            yield return ("device: observed numeric target identity", () =>
            {
                var device = NumericArticle("65710155");
                Check(device.IsSupportedTestColumn && device.ArticleNumber == "65710155" && device.Model == "8WD4613-5JH37");
            });
            yield return ("device: numeric target still requires variant 21", () =>
            {
                foreach (int variant in new[] { 20, 50, 51, 0 })
                    Check(!NumericArticle("65710155", variant).IsSupportedTestColumn);
            });
            yield return ("device: neighboring numeric article is not target", () => Check(!NumericArticle("65711155").IsSupportedTestColumn));
            yield return ("device: fractional and exponent articles are not normalized", () =>
            {
                foreach (string article in new[] { "65710155.0", "65710155.1", "6.5710155e7", "-65710155" })
                    Check(!NumericArticle(article).IsSupportedTestColumn);
            });
            yield return ("device: arbitrary article punctuation is not normalized", () =>
            {
                foreach (string article in new[] { "6571.0155", "657-101-55", "065710155", "65710155extra" })
                    Check(!Parse(article).IsSupportedTestColumn);
            });
            yield return ("device: real-shaped chunked identity reply", () =>
            {
                var body = Encoding.UTF8.GetBytes("{\"firmwareVersion\":\"v1.0.3\",\"articleNumber\":65710155,\"firstConfig\":false,\"powerMode\":0,\"hardware\":{\"hardwareVariant\":21,\"hardwareRevision\":0,\"mac\":\"00:00:00:00:00:00\"},\"siren\":{\"firmwareVersion\":\"0.0.0\"}}");
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nTransfer-Encoding: chunked\r\n\r\n{body.Length:x}\r\n");
                using var stream = new MemoryStream(header.Concat(body).Concat(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n")).ToArray());
                var response = SiemensWire.ReadResponse(stream);
                var device = SiemensDeviceInfo.Parse(response.Body);
                Check(response.StatusCode == 200 && response.Body.SequenceEqual(body) && device.IsSupportedTestColumn
                    && device.FirmwareVersion == "v1.0.3" && device.FirstConfig == false);
            });
            yield return ("device: normalized identity", () => Check(Parse(" 8wd4613-5jh37 ").IsSupportedTestColumn));
            yield return ("device: other variant is not target", () => Check(!Parse("8WD4613-5JH37", 20).IsSupportedTestColumn));
            yield return ("device: other article is not target", () => Check(!Parse("657.111.55").IsSupportedTestColumn));
            yield return ("device: unknown article retained", () => Check(Parse("unknown").Model == "UNKNOWN"));
            yield return ("device: false firstConfig flag retained", () => Check(Parse("657.101.55").FirstConfig == false));
            yield return ("device: optional firstConfig absent", () => Check(Read("{\"articleNumber\":\"657.101.55\",\"hardware\":{\"hardwareVariant\":21}}").FirstConfig == null));
            yield return ("device: numeric article cannot imply target", () => Check(!Read("{\"articleNumber\":657,\"hardware\":{\"hardwareVariant\":21}}").IsSupportedTestColumn));
            yield return ("device: unknown hardware fields permitted", () => Check(Read("{\"articleNumber\":\"657.101.55\",\"hardware\":{\"hardwareVariant\":21,\"mac\":1234567890123456789}}").IsSupportedTestColumn));
            yield return ("device: missing identity rejected", () => Reject("{\"hardware\":{\"hardwareVariant\":21}}"));
            yield return ("device: missing hardware rejected", () => Reject("{\"articleNumber\":\"657.101.55\"}"));
            yield return ("device: duplicate identity rejected", () => Reject("{\"articleNumber\":\"657.101.55\",\"articleNumber\":\"other\",\"hardware\":{\"hardwareVariant\":21}}"));
            yield return ("device: duplicate hardware rejected", () => Reject("{\"articleNumber\":\"657.101.55\",\"hardware\":{\"hardwareVariant\":21,\"hardwareVariant\":20}}"));
            yield return ("device: invalid variant rejected", () => Reject("{\"articleNumber\":\"657.101.55\",\"hardware\":{\"hardwareVariant\":\"21\"}}"));
            yield return ("device: nonboolean flag rejected", () => Reject("{\"articleNumber\":\"657.101.55\",\"firstConfig\":1,\"hardware\":{\"hardwareVariant\":21}}"));
            yield return ("device: empty article rejected", () => Reject("{\"articleNumber\":\" \",\"hardware\":{\"hardwareVariant\":21}}"));
            yield return ("device: oversized information rejected", () => Reject(new string(' ', 65537)));
        }
    }

    private static SiemensDeviceInfo Parse(string article, int variant = 21) => Read(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            articleNumber = article, firmwareVersion = "test-only", firstConfig = false,
            hardware = new { hardwareVariant = variant }
        }));
    private static SiemensDeviceInfo Read(string json) => SiemensDeviceInfo.Parse(Encoding.UTF8.GetBytes(json));
    private static SiemensDeviceInfo NumericArticle(string literal, int variant = 21) =>
        Read("{\"articleNumber\":" + literal + ",\"hardware\":{\"hardwareVariant\":" + variant + "}}");
    private static void Check(bool condition) { if (!condition) throw new Exception("Unexpected parsed device identity."); }
    private static void Reject(string json)
    {
        try { Read(json); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { return; }
        throw new Exception("Invalid device information was accepted.");
    }
}
