namespace SafetyGateSupport.Models;
public class AppSettings
{
    public string ThemeMode { get; set; } = "Light";
    public string StockroomPlantId { get; set; } = "";
    public string SharedRoot { get; set; } = @"Q:\SafetyGateSupport";
    public string SiemensExePath { get; set; } = "";
    public string BusLinkExePath { get; set; } = "";
    public string UamExePath { get; set; } = "";
}
