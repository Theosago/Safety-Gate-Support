using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SafetyGateSupport.Services;

internal sealed record OperatorDrivePilot(string SharedRoot, string GateId, string UnitId,
    string Asset, string Unit, string RelativePdf, string DriveRoot, string? FileId,
    string? LastHash = null, string? Link = null, string? PendingFileId = null, bool Paused = false)
{
    internal const int MaximumPdfBytes = 25 * 1024 * 1024;
    internal static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafetyGateSupport", "operator-drive-pilot.json");

    internal bool CanEditMapping => LastHash is null && PendingFileId is null;

    internal bool Matches(string root, string gate, string unit) =>
        string.Equals(SharedRoot, root, StringComparison.OrdinalIgnoreCase) && GateId == gate && UnitId == unit;

    internal static OperatorDrivePilot? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<OperatorDrivePilot>(File.ReadAllText(path)) : null;

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal async Task<string> SyncAsync(OperatorDriveClient drive, string statePath, CancellationToken token)
    {
        string local = Path.GetFullPath(RelativePdf, SharedRoot);
        if (Path.IsPathRooted(RelativePdf) || Path.GetRelativePath(SharedRoot, local).Split(Path.DirectorySeparatorChar)[0] == ".."
            || !Path.GetExtension(local).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select an operator PDF inside the shared data folder.");
        byte[] bytes = await ReadPdfAsync(local, token);
        string localHash = Hash(bytes);
        var root = await drive.GetAsync(DriveRoot, token);
        if (root.DriveId is null || root.MimeType != OperatorDriveClient.FolderMime || root.Trashed)
            throw new InvalidOperationException("The target must be a folder in a Google Shared Drive.");
        bool linked = FileId is not null || PendingFileId is not null;
        var asset = await drive.FolderAsync(root, Asset, create: !linked, token);
        var unit = await drive.FolderAsync(asset, Unit, create: !linked, token);
        var state = this;
        var remote = FileId is not null
            ? await drive.GetAsync(FileId, token)
            : PendingFileId is not null
                ? await drive.GetPendingAsync(PendingFileId, token)
                : await drive.FindAsync(unit, Path.GetFileName(local), token);
        string result = "Operator PDF is in sync.";

        if (remote is null)
        {
            string id = PendingFileId ?? await drive.GenerateIdAsync(token);
            state = this with { PendingFileId = id, LastHash = localHash };
            state.Save(statePath);
            remote = await drive.CreatePdfAsync(id, unit.Id, Path.GetFileName(local), bytes, token);
            result = "Published operator PDF.";
            if (remote.Id != id || remote.Md5Checksum != localHash)
                throw new IOException("Drive did not confirm the created PDF ID and checksum.");
        }

        if (remote.Trashed || remote.DriveId != unit.DriveId || remote.Parents?.Contains(unit.Id) != true ||
            remote.MimeType != "application/pdf" || remote.Md5Checksum is null ||
            (PendingFileId is not null && remote.Id != PendingFileId))
            throw new InvalidOperationException("The linked PDF must be in the selected Asset/Unit folder. Sync paused.");

        string remoteHash = remote.Md5Checksum;
        if (localHash != remoteHash)
        {
            if (LastHash is null || (localHash != LastHash && remoteHash != LastHash))
                throw new InvalidOperationException("The shared-folder and Drive PDFs differ. Sync paused. Review both copies and make them match to resume.");

            if (remoteHash == LastHash)
            {
                if (Hash(await ReadPdfAsync(local, token)) != localHash)
                    throw new IOException("The shared PDF changed during sync. Sync will retry.");
                await drive.CheckUnchangedAsync(remote, token);
                remote = await drive.UpdatePdfAsync(remote.Id, bytes, remote.ETag, token);
                result = "Updated the existing Drive PDF.";
                if (remote.Md5Checksum != localHash)
                    throw new IOException("Drive did not confirm the uploaded PDF checksum.");
            }
            else
            {
                byte[] downloaded = await drive.DownloadAsync(remote.Id, token);
                ValidatePdf(downloaded);
                if (Hash(downloaded) != remoteHash) throw new IOException("Drive changed during download. Sync will retry.");
                await drive.CheckUnchangedAsync(remote, token);
                await ReplaceLocalAsync(local, downloaded, localHash, token);
                localHash = remoteHash;
                result = "Applied the Drive update to the shared operator PDF and archived the prior copy.";
            }
        }

        (state with
        {
            FileId = remote.Id, PendingFileId = null, LastHash = localHash,
            Link = remote.WebViewLink ?? state.Link ?? $"https://drive.google.com/file/d/{remote.Id}/view"
        }).Save(statePath);
        return result;
    }

    private static async Task ReplaceLocalAsync(string path, byte[] bytes, string expectedHash, CancellationToken token)
    {
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string archive = Path.Combine(Path.GetDirectoryName(path)!, ".operator-pdf-history");
        try
        {
            await File.WriteAllBytesAsync(staged, bytes, token);
            Directory.CreateDirectory(archive);
            string backup = Path.Combine(archive, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.pdf");
            if (Hash(await ReadPdfAsync(path, token)) != expectedHash)
                throw new IOException("The shared PDF changed during sync. Sync will retry.");
            token.ThrowIfCancellationRequested();
            File.Replace(staged, path, backup);
            if (Hash(await ReadPdfAsync(backup, token)) != expectedHash)
                throw new IOException("The shared PDF changed during replacement. That copy is in .operator-pdf-history. Review both copies.");
        }
        finally { File.Delete(staged); }
    }

    private static async Task<byte[]> ReadPdfAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValidatePdf(await ReadLimitedAsync(stream, token));
    }

    internal static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > MaximumPdfBytes) throw new IOException("Operator PDFs are limited to 25 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static byte[] ValidatePdf(byte[] bytes) => bytes.AsSpan().StartsWith("%PDF-"u8)
        ? bytes : throw new IOException("The selected operator file does not have a PDF header.");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
}
