using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace SafetyGateSupport.Services;

internal static class OperatorPartsSchedule
{
    internal const string Argument = "--publish-spare-parts";
    private const string TaskPrefix = "Safety Gate Support - Spare parts refresh";

    internal static async Task InstallAsync(CancellationToken token)
    {
        string path = Environment.ProcessPath ?? throw new IOException("The app's location could not be found.");
        using var account = WindowsIdentity.GetCurrent();
        string user = account.User?.Value ?? throw new IOException("The current Windows account could not be identified.");
        string taskFile = Path.Combine(Path.GetTempPath(), "SafetyGateParts-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            CreateTask(path, user, DateTimeOffset.Now.AddMinutes(1)).Save(taskFile);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "/Create", "/TN", TaskPrefix + " - " + user, "/XML", taskFile, "/F" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Windows Task Scheduler could not start.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw;
            }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
                throw new IOException("Windows could not schedule spare-parts refresh. " + (error.Result + " " + output.Result).Trim());
        }
        finally { File.Delete(taskFile); }
    }

    internal static XDocument CreateTask(string executable, string userSid, DateTimeOffset start)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new(new XElement(ns + "Task", new XAttribute("version", "1.3"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Description", "Refreshes the spare-parts PDFs in previously published unit folders.")),
            new XElement(ns + "Triggers", new XElement(ns + "TimeTrigger",
                new XElement(ns + "Enabled", true),
                new XElement(ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)),
                new XElement(ns + "Repetition", new XElement(ns + "Interval", XmlConvert.ToString(OperatorSpareParts.ScanInterval))))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Publisher"),
                new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "StartWhenAvailable", true),
                new XElement(ns + "ExecutionTimeLimit", "PT15M")),
            new XElement(ns + "Actions", new XAttribute("Context", "Publisher"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executable), new XElement(ns + "Arguments", Argument)))));
    }

    internal static async Task<int> RunAsync()
    {
        string message;
        int exitCode = 1;
        try
        {
            var data = new DataService(initializeSharedDefaults: false);
            if (!data.IsReady) throw new IOException("The scheduled refresh could not reach the shared library.");
            if (!OperatorDriveRunner.CanPublish) throw new IOException("Sign in to Google Drive on this Windows account.");
            var state = OperatorPublishingState.Load(data.SharedRoot);
            if (state.Root is not { Pending: false })
                throw new IOException("Publish the unit folders before running the scheduled refresh.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(14));
            string result = await OperatorDriveRunner.RunAsync(data, token: timeout.Token, sparePartsOnly: true);
            message = result.Length == 0 ? "Spare-parts check skipped. It will retry on the next schedule." : result;
            exitCode = 0;
        }
        catch (Exception error) { message = error.Message; }

        try
        {
            Directory.CreateDirectory(OperatorPublishingState.LocalDirectory);
            File.WriteAllText(Path.Combine(OperatorPublishingState.LocalDirectory, "spare-parts-refresh.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { exitCode = 1; }
        return exitCode;
    }
}
