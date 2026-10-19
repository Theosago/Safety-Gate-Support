using System.Text.Json;

namespace SafetyGateSupport.Siemens;

public sealed record SiemensDeviceInfo(
    string ArticleNumber,
    string Model,
    string? FirmwareVersion,
    int HardwareVariant,
    bool? FirstConfig)
{
    public bool IsSupportedTestColumn => Model == "8WD4613-5JH37" && HardwareVariant == 21;

    public static SiemensDeviceInfo Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 64 * 1024)
            throw new InvalidDataException("Device information exceeds the 64 KiB diagnostic limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = RequireObject(document.RootElement);
        var article = Required(root, "articleNumber");
        string rawArticle = article.ValueKind switch
        {
            JsonValueKind.String => article.GetString()!.Trim(),
            JsonValueKind.Number => article.GetRawText(),
            _ => throw new InvalidDataException("Device articleNumber must be a string or number.")
        };
        if (string.IsNullOrWhiteSpace(rawArticle) || rawArticle.Length > 128)
            throw new InvalidDataException("Device articleNumber is empty or too long.");
        string model = rawArticle.ToUpperInvariant() switch
        {
            "657.001.55" => "8WD4613-5HH37",
            "657.011.55" => "8WD4613-5HH47",
            "657.101.55" => "8WD4613-5JH37",
            "657.111.55" => "8WD4613-5JH47",
            "657.501.55" => "8WD4615-5HH37",
            "657.511.55" => "8WD4615-5HH47",
            "657.601.55" => "8WD4615-5JH37",
            "657.611.55" => "8WD4615-5JH47",
            var value => value
        };
        var hardware = RequireObject(Required(root, "hardware"));
        var variant = Required(hardware, "hardwareVariant");
        if (variant.ValueKind != JsonValueKind.Number || !variant.TryGetInt32(out int hardwareVariant))
            throw new InvalidDataException("Device hardwareVariant must be an integer.");

        string? firmware = null;
        if (root.TryGetProperty("firmwareVersion", out var firmwareValue))
        {
            if (firmwareValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Device firmwareVersion must be a string.");
            firmware = firmwareValue.GetString();
        }
        bool? firstConfig = null;
        if (root.TryGetProperty("firstConfig", out var firstValue))
            firstConfig = firstValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException("Device firstConfig must be a boolean.")
            };
        return new SiemensDeviceInfo(rawArticle, model, firmware, hardwareVariant, firstConfig);
    }

    private static JsonElement RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Device information must contain JSON objects.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException($"Duplicate device information property: {property.Name}.");
        return element;
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value :
            throw new InvalidDataException($"Device information is missing {name}.");
}
