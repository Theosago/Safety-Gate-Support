using SafetyGateSupport.Services;
using SafetyGateSupport.TestFixtures;
namespace SafetyGateSupport.Validation;
internal static class DrawingOrientationChecks
{
    public static void Check()
    {
        foreach (int angle in new[] { 0, 90, 180, 270 }) foreach (int metadata in new[] { 0, 90, 180, 270 })
        {
            var pdf = DrawingPdfFixture.TextPage(angle, metadata);
            bool portrait = (angle is 90 or 270) != (metadata is 90 or 270);
            var result = PdfTextOrientation.Detect(pdf, 0, portrait ? 90 : 0, CancellationToken.None);
            int expected = (angle - metadata + 360) % 360;
            if (result?.Angle != expected) throw new InvalidOperationException($"Text {angle}, metadata {metadata}: expected {expected}, got {result?.Angle}.");
        }
    }
}
