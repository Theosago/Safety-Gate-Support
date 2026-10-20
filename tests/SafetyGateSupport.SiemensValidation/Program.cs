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
            }
        }
        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
