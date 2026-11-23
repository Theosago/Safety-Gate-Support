using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SafetyGateSupport.Services;

internal static class OperatorPartsSchedule
{
    internal const string Argument = "--publish-spare-parts";
    private const string TaskPrefix = "Safety Gate Support - Spare parts refresh";
    private const int DailyTrigger = 2, ExecuteAction = 0, InteractiveToken = 3, CreateOrUpdate = 6;
    private const int LeastPrivilege = 0, IgnoreNew = 2;

    internal static Task InstallAsync(CancellationToken token)
    {
        string path = Environment.ProcessPath ?? throw new IOException("The app's location could not be found.");
        using var account = WindowsIdentity.GetCurrent();
        string user = account.User?.Value ?? throw new IOException("The current Windows account could not be identified.");
        return Task.Run(() => RegisterDailyTask(TaskPrefix + " - " + user, path, user, DateTime.Now.AddMinutes(1)), token);
    }

    internal static void RegisterDailyTask(string name, string executable, string userSid, DateTime start)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new IOException("Windows Task Scheduler is unavailable.");
        try
        {
            dynamic scheduler = Activator.CreateInstance(schedulerType)!;
            scheduler.Connect();
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "Refreshes the spare-parts PDFs in previously published unit folders.";
            task.Principal.UserId = userSid;
            task.Principal.LogonType = InteractiveToken;
            task.Principal.RunLevel = LeastPrivilege;
            task.Settings.MultipleInstances = IgnoreNew;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.StartWhenAvailable = true;
            task.Settings.ExecutionTimeLimit = "PT15M";

            dynamic trigger = task.Triggers.Create(DailyTrigger);
            trigger.StartBoundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            trigger.DaysInterval = (short)1;
            dynamic action = task.Actions.Create(ExecuteAction);
            action.Path = executable;
            action.Arguments = Argument;
            action.WorkingDirectory = Path.GetDirectoryName(executable);
            scheduler.GetFolder(@"\").RegisterTaskDefinition(name, task, CreateOrUpdate, userSid, null, InteractiveToken, null);
        }
        catch (COMException error)
        {
            throw new IOException("Windows could not schedule spare-parts refresh. " + error.Message, error);
        }
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
