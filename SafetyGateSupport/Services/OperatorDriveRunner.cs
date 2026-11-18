using System.IO;
using System.Net.Http;
using Google.Apis.Auth.OAuth2;

namespace SafetyGateSupport.Services;

internal static class OperatorDriveRunner
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly SemaphoreSlim Mutex = new(1);
    private static GoogleCredential? _credential;
    internal static string Status { get; private set; } = "";

    internal static async Task RunAsync(string root, Func<OperatorDrivePilot?, OperatorDrivePilot>? configure = null)
    {
        if (configure is null && !File.Exists(OperatorDrivePilot.StatePath)) return;
        if (configure is null)
        {
            if (!await Mutex.WaitAsync(0)) return;
        }
        else await Mutex.WaitAsync();
        try
        {
            Status = await Task.Run(async () =>
            {
                string path = OperatorDrivePilot.StatePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var pilot = OperatorDrivePilot.Load(path);
                if (configure is not null)
                {
                    pilot = configure(pilot);
                    pilot.Save(path);
                }
                if (pilot is null || !pilot.SharedRoot.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
                if (pilot.Paused) return "Automatic sync is paused.";
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var drive = new OperatorDriveClient(Http, AccessTokenAsync);
                return await pilot.SyncAsync(drive, path, timeout.Token);
            });
        }
        catch (OperationCanceledException) { Status = "Drive sync timed out. It will retry automatically."; }
        catch (Exception error) { Status = error.Message; }
        finally { Mutex.Release(); }
    }

    private static async Task<string> AccessTokenAsync(CancellationToken token)
    {
        if (_credential is null)
        {
            string? path = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Set GOOGLE_APPLICATION_CREDENTIALS to the Shared Drive service account JSON path, then restart the app.");
            _credential = CredentialFactory.FromFile<ServiceAccountCredential>(path)
                .ToGoogleCredential().CreateScoped("https://www.googleapis.com/auth/drive");
        }
        return await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: token);
    }
}
