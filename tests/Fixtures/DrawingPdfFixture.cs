using System.Text;
namespace SafetyGateSupport.TestFixtures;
internal static class DrawingPdfFixture
{
    public static byte[] TextPage(int textAngle, int pageRotation = 0)
    {
        bool portrait = textAngle is 90 or 270; int width = portrait ? 500 : 800, height = portrait ? 800 : 500;
        string matrix = textAngle switch { 90 => "0 1 -1 0 500 0", 180 => "-1 0 0 -1 800 500", 270 => "0 -1 1 0 0 800", _ => "1 0 0 1 0 0" };
        var content = new StringBuilder("q " + matrix + " cm\n0 0 0 rg\nBT /F1 28 Tf\n");
        string[] lines = ["ELECTRICAL SCHEMATIC", "SAFETY GATE CONTROL PANEL", "POWER SUPPLY AND MOTOR CIRCUIT", "TERMINAL CONNECTION DRAWING", "ASSET NUMBER 1223 UNIT 1", "REVISION APPROVED FOR MAINTENANCE"];
        for (int i = 0; i < lines.Length; i++) content.Append($"1 0 0 1 60 {430 - 55 * i} Tm ({lines[i]}) Tj\n");
        content.Append("ET Q\n");
        string stream = content.ToString();
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Rotate {pageRotation} /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>", $"<< /Length {stream.Length} >>\nstream\n{stream}endstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
