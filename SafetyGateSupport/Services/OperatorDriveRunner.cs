using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using SafetyGateSupport.Models;
using UglyToad.PdfPig;

namespace SafetyGateSupport.Services;

internal static class OperatorDriveRunner
{
    internal const string QueuedMessage = "Drive publication queued.";
    internal const string PublishedMessage = "Google Drive is up to date.";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly SemaphoreSlim Mutex = new(1);
    private static GoogleCredential? _serviceAccount;
    private static UserCredential? _signedIn;
    private static bool _needsSignIn;

    internal static bool HasSavedClient => OperatorDriveSignIn.HasClient;
    internal static bool CanPublish => !_needsSignIn && (_signedIn is not null || HasSavedClient ||
        File.Exists(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")));
    internal static string Status { get; private set; } = "";

    internal static bool IsSupported(DataService data) => !data.IsHomePreview && !data.IsOnboardingDemo;

    internal static async Task<string> RunAsync(DataService data, bool publishNow = false, string? requestedRoot = null,
        IProgress<OperatorPublishProgress>? progress = null, CancellationToken token = default)
    {
        if (!IsSupported(data)) return "";
        string root = Path.GetFullPath(data.SharedRoot);
        if (!publishNow)
        {
            if (!CanPublish || !File.Exists(OperatorPublishingState.StatePath(root)) &&
                !File.Exists(OperatorPublishingState.LegacyPath)) return "";
            if (!await Mutex.WaitAsync(0, token)) return "";
        }
        else await Mutex.WaitAsync(token);

        try
        {
            Status = await Task.Run(async () =>
            {
                if (!data.IsReady || !Path.GetFullPath(data.SharedRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Connect the shared library before publishing.");
                var path = OperatorPublishingState.StatePath(root);
                var state = OperatorPublishingState.Load(root);
                if (!publishNow && state.Root is null && !File.Exists(OperatorPublishingState.LegacyPath)) return "";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                state = OperatorPublishingState.Load(root);
                if (state.ImportPilot(root)) state.Save(root);
                var drive = new OperatorDriveClient(Http, AccessTokenAsync);
                if (publishNow)
                {
                    if (!CanPublish) throw new InvalidOperationException("Sign in with Google to publish.");
                    if (requestedRoot is not null && state.Root is not null && state.Root.Id != requestedRoot)
                        throw new InvalidOperationException("Keep the current Drive folder to preserve the printed QR codes.");
                    if (requestedRoot is not null && state.Root is null)
                    {
                        var folder = await drive.GetAsync(requestedRoot, token);
                        if (folder.Trashed || folder.MimeType != OperatorDriveClient.FolderMime)
                            throw new InvalidOperationException("Choose an active folder in Google Drive.");
                        state.Root = new(folder.Id, OperatorDriveClient.FolderLink(folder));
                    }
                    state.PublishAll = true;
                    state.Save(root);
                }
                if (!state.PublishAll && !state.Units.Values.Any(unit => !unit.Paused)) return "";
                var publisher = new OperatorDrivePublisher(drive, LoadPhotoAsync);
                return (await publisher.RunAsync(data, root, state, publishNow, progress, token)).Message;
            }, token);
        }
        catch (OperationCanceledException) { Status = "Publishing stopped. Pending units will be retried automatically."; }
        catch (Exception error) { Status = error.Message; }
        finally { Mutex.Release(); }
        return Status;
    }

    internal static async Task<string> AfterDrawingSavedAsync(DataService data, string unitId)
    {
        if (!IsSupported(data)) return "";
        try
        {
            bool enabled = await Task.Run(() =>
            {
                string root = Path.GetFullPath(data.SharedRoot);
                var state = OperatorPublishingState.Load(root);
                state.ImportPilot(root);
                return state.PublishAll || state.Units.TryGetValue(unitId, out var unit) && !unit.Paused;
            });
            if (!enabled) return "";
            if (CanPublish) _ = RunAsync(data);
            return QueuedMessage;
        }
        catch (Exception error) { return "Drive publication status unavailable. " + error.Message; }
    }

    internal static async Task<string?> DrawingStatusAsync(DataService data, string unitId)
    {
        try
        {
            return await Task.Run<string?>(() =>
            {
                var state = OperatorPublishingState.Load(data.SharedRoot);
                if (!state.Units.TryGetValue(unitId, out var entry)) return null;
                if (entry.Error is not null) return entry.Error;
                if (entry.Fingerprint is null) return null;

                using var lease = data.AcquireAssetWriteLock();
                var item = OperatorPublicationSources.ListUnits(data).FirstOrDefault(item => item.Unit.Id == unitId);
                if (item is null || item.Gate.Id != entry.GateId) return null;
                var current = OperatorPublicationSources.Read(data, item, data.LoadResources(), entry, default);
                return current.Fingerprint == entry.Fingerprint ? PublishedMessage : null;
            });
        }
        catch (IOException) { return null; }
        catch (Exception error) { return error.Message; }
    }

    internal static async Task SaveGuidePdfAsync(DataService data, SafetySystem unit, string source)
    {
        if (!IsSupported(data)) throw new InvalidOperationException("Use a connected plant library to add a guide PDF.");
        await Task.Run(() =>
        {
            var bytes = OperatorPublicationSources.ReadPdf(source);
            using (var document = PdfDocument.Open(bytes))
                if (document.NumberOfPages == 0) throw new InvalidDataException("The selected PDF has no pages.");
            using var lease = data.AcquireAssetWriteLock();
            string folder = OperatorPublishingState.AttachmentsPath(data.SharedRoot, unit.Id);
            string destination = Path.Combine(folder, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                string archive = Path.Combine(folder, "Archive");
                Directory.CreateDirectory(archive);
                File.Copy(destination, Path.Combine(archive, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source)));
            }
            OperatorPublishingState.WriteFile(destination, bytes);
        });
        _ = RunAsync(data);
    }

    internal static async Task SignInAsync(string? path, CancellationToken token)
    {
        await Mutex.WaitAsync(token);
        try
        {
            var previous = _signedIn;
            _signedIn = await OperatorDriveSignIn.ConnectAsync(path, token);
            previous?.Flow.Dispose();
            _needsSignIn = false;
            Status = "Connected to Google Drive.";
        }
        finally { Mutex.Release(); }
    }

    private static async Task<string> AccessTokenAsync(CancellationToken token)
    {
        _signedIn ??= await OperatorDriveSignIn.LoadAsync(token);
        if (_signedIn is not null)
        {
            try { return await _signedIn.GetAccessTokenForRequestAsync(cancellationToken: token); }
            catch (TokenResponseException error) when (error.Error?.Error is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                _needsSignIn = true;
                throw new DriveAuthenticationException("Google sign-in has expired. Open Publish & print QR and reconnect to Google.");
            }
        }
        if (_serviceAccount is null)
        {
            string? path = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new DriveAuthenticationException("Open Publish & print QR and sign in with Google.");
            _serviceAccount = CredentialFactory.FromFile<ServiceAccountCredential>(path)
                .ToGoogleCredential().CreateScoped(OperatorDriveSignIn.Scope);
        }
        return await _serviceAccount.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: token);
    }

    private static async Task<UnitGuidePhoto?> LoadPhotoAsync(string path, CancellationToken token)
    {
        var photo = await IdentificationPhotoLoader.LoadAsync(path, 1600, token);
        if (photo.Source is null) return null;
        var jpeg = new JpegBitmapEncoder { QualityLevel = 88 };
        jpeg.Frames.Add(BitmapFrame.Create(photo.Source));
        using var output = new MemoryStream();
        jpeg.Save(output);
        return new(output.ToArray(), photo.Source.PixelWidth, photo.Source.PixelHeight);
    }
}
