using UglyToad.PdfPig;
namespace SafetyGateSupport.Services;

public static class PdfTextOrientation
{
    public sealed record ReadingDirection(int Angle, double Confidence, int Characters);
    public static ReadingDirection? Detect(byte[] pdf, int pageNumber, int landscapeAngle, CancellationToken token)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(pageNumber + 1);
        var votes = new double[4]; int characters = 0;
        foreach (var letter in page.Letters.Take(50000))
        {
            token.ThrowIfCancellationRequested();
            if (!letter.Value.Any(char.IsLetterOrDigit)) continue;
            double dx = letter.EndBaseLine.X - letter.StartBaseLine.X, dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
            if (dx * dx + dy * dy < 0.0001) continue;
            double degrees = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            int angle = ((int)Math.Round(degrees / 90) % 4) * 90;
            double difference = Math.Abs(degrees - angle); difference = Math.Min(difference, 360 - difference);
            if (difference > 12) continue;
            votes[angle / 90] += letter.Value.Any(char.IsLetter) ? 1 : 0.3;
            characters++;
        }
        int a = landscapeAngle / 90, b = (a + 2) % 4;
        double total = votes[a] + votes[b]; if (total < 12) return null;
        int best = votes[a] >= votes[b] ? a : b; double confidence = votes[best] / total;
        return confidence >= 0.7 ? new(best * 90, confidence, characters) : null;
    }
}
