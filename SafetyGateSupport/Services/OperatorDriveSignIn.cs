using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Util.Store;

namespace SafetyGateSupport.Services;

internal static class OperatorDriveSignIn
{
    internal const string Scope = "https://www.googleapis.com/auth/drive";
    private const string ClientKey = "desktop-client";
    private static readonly LocalStore Store = new();

    internal static async Task<UserCredential?> LoadAsync(CancellationToken token)
    {
        var client = await Store.GetAsync<ClientSecrets>(ClientKey);
        if (client is null) return null;
        var flow = new GoogleAuthorizationCodeFlow(CreateFlow(client, Store));
        try
        {
            var saved = await flow.LoadTokenAsync(client.ClientId, token);
            if (saved is not null) return new UserCredential(flow, client.ClientId, saved);
        }
        catch
        {
            flow.Dispose();
            throw;
        }
        flow.Dispose();
        throw new InvalidOperationException("Use Sign in with Google to reconnect the publisher.");
    }

    internal static async Task<UserCredential> ConnectAsync(string jsonPath, CancellationToken token)
    {
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(jsonPath, token));
        if (!json.RootElement.TryGetProperty("installed", out var installed) ||
            installed.ValueKind != JsonValueKind.Object ||
            !installed.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String ||
            !installed.TryGetProperty("client_secret", out var secret) || secret.ValueKind != JsonValueKind.String ||
            id.GetString() is not { Length: > 0 } clientId || secret.GetString() is not { Length: > 0 } clientSecret)
            throw new InvalidOperationException("Select the JSON downloaded for your Google Desktop app OAuth client.");

        var client = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        // Keep any existing working session intact until a new browser sign-in succeeds.
        var initializer = CreateFlow(client, null);
        initializer.Prompt = "consent select_account";
        using var interactiveFlow = new PkceGoogleAuthorizationCodeFlow(initializer);
        var app = new AuthorizationCodeInstalledApp(interactiveFlow, new LocalServerCodeReceiver(
            "<html><body>Return to Safety Gate Support to continue.</body></html>", LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp));
        var signedIn = await app.AuthorizeAsync(clientId, token);
        if (string.IsNullOrEmpty(signedIn.Token.RefreshToken))
            throw new InvalidOperationException("Google did not grant offline access. Reconnect and approve Drive access.");
        var granted = signedIn.Token.Scope;
        if (!string.IsNullOrWhiteSpace(granted) && !granted.Split(' ').Contains(Scope, StringComparer.Ordinal))
            throw new InvalidOperationException("Approve Google Drive access during sign-in to enable publishing.");

        await Store.StoreAsync(clientId, signedIn.Token);
        await Store.StoreAsync(ClientKey, client);
        var flow = new GoogleAuthorizationCodeFlow(CreateFlow(client, Store));
        return new UserCredential(flow, clientId, signedIn.Token);
    }

    private static GoogleAuthorizationCodeFlow.Initializer CreateFlow(ClientSecrets client, IDataStore? store) =>
        new() { ClientSecrets = client, Scopes = new[] { Scope }, DataStore = store };

    private sealed class LocalStore : IDataStore
    {
        private static readonly string DirectoryPath = Path.Combine(
            Path.GetDirectoryName(OperatorDrivePilot.StatePath)!, "drive-login");

        private static string FilePath<T>(string key)
        {
            string name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(typeof(T).FullName + ":" + key)));
            return Path.Combine(DirectoryPath, name);
        }

        public Task StoreAsync<T>(string key, T value)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = FilePath<T>(key);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            string path = FilePath<T>(key);
            if (!File.Exists(path)) return Task.FromResult<T>(default!);
            byte[] payload = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return Task.FromResult(JsonSerializer.Deserialize<T>(payload)!);
        }

        public Task DeleteAsync<T>(string key)
        {
            File.Delete(FilePath<T>(key));
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
            return Task.CompletedTask;
        }
    }
}
