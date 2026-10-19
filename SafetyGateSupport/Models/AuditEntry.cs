namespace SafetyGateSupport.Models;

public class AuditEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string User { get; set; } = Environment.UserName;
    public string Machine { get; set; } = Environment.MachineName;
    public string Action { get; set; } = "";
    public string GateId { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Details { get; set; } = "";
    public string Display => $"{TimestampUtc.ToLocalTime():g}  {User}@{Machine}  {Action}  {Details}";
}
