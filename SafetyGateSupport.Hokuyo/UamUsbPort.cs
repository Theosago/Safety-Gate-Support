namespace SafetyGateSupport.Hokuyo;
public sealed record UamUsbPort(string PortName, string DeviceInstanceId, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public sealed record UamReadResult(bool Success, string Summary, string ReportPath);
