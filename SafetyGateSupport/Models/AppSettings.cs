namespace SafetyGateSupport.Models;
public class AppSettings
{
    public string SharedRoot { get; set; } = @"Q:\SafetyGateSupport";
    public string SiemensExePath { get; set; } = "";
    public string BusLinkExePath { get; set; } = "";
    public string UamExePath { get; set; } = "";
}
