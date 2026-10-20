namespace SafetyGateSupport.SiemensValidation;

internal static class Program
{
    private static int Main()
    {
        int passed = 0, failed = 0;
        foreach (var (name, run) in WireChecks.Cases.Concat(SignalArchiveChecks.Cases).Concat(DeviceInfoChecks.Cases).Concat(UploadChecks.Cases))
        {
            try
            {
                run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                {
                    var detail = $"{name}: {exception}".Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                    Console.WriteLine($"::error title=Siemens validation::{detail}");
                }
            }
        }
        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
