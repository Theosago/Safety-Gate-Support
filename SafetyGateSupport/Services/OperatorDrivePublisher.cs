using System.IO;
using System.Security.Cryptography;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

internal sealed class DriveAuthenticationException(string message) : Exception(message);

internal sealed record OperatorPublishProgress(int Completed, int Total, string Unit);
internal sealed record OperatorPublishSummary(int Published, int Current, int Issues)
{
    internal string Message => Issues > 0
        ? $"{Published + Current} current. {Issues} need attention and will be checked again automatically."
        : Published > 0 ? $"{Published} published or updated. {Current} already current." : "All selected units are up to date.";
}

internal sealed class OperatorDrivePublisher(
    OperatorDriveClient drive,
    Func<string, CancellationToken, Task<UnitGuidePhoto?>> loadPhoto)
{
    internal const string ElectricalName = "01 Electrical drawing.pdf";
    internal const string GuideName = "02 Unit guide.pdf";

    internal async Task PublishSparePartsAsync(DataService data, string sharedRoot, OperatorPublishingState state,
        Func<string, IReadOnlyCollection<string>, CancellationToken, Task<StockroomService.Snapshot>> reader,
        DateTimeOffset now, CancellationToken token, bool forceScan = false)
    {
        if (state.Root is not { Pending: false } boundRoot) return;
        OperatorPublicationUnit[] units;
        List<PartRecord> catalog;
        try
        {
            using var library = data.AcquireAssetWriteLock();
            units = OperatorPublicationSources.ListUnits(data).Where(item =>
                state.Units.TryGetValue(item.Unit.Id, out var entry) &&
                entry.Folder is { Pending: false } && entry.GateId == item.Gate.Id &&
                (state.PublishAll || !entry.Paused)).ToArray();
            catalog = data.LoadPartsForPublishing();
            SetCatalogError(null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            SetCatalogError("Spare parts could not be read from the shared library. " + error.Message);
            return;
        }

        var targets = units.Select(item => (Item: item, Parts: DataService.ReplacementPartsFor(item.Unit, catalog).ToArray())).ToArray();
        var plant = StockroomPlant.Find(state.StockroomPlantId);
        OperatorStockroomScan? scan = null;
        if (plant is not null)
        {
            var ids = targets.SelectMany(target => target.Parts).Select(part => part.PartNumber.Trim())
                .Where(StockroomService.IsItemId).DistinctBy(StockroomService.StockKey).ToArray();
            scan = state.StockroomScans.GetValueOrDefault(plant.Id);
            if (ids.Length > 0)
            {
                var latest = await OperatorSpareParts.ScanAsync(plant, ids, scan, reader, now, token, forceScan);
                if (!ReferenceEquals(latest, scan))
                {
                    scan = latest;
                    state.StockroomScans[plant.Id] = latest;
                    state.Save(sharedRoot);
                }
            }
        }

        OperatorDriveClient.Item? root = null;
        var assets = new Dictionary<string, OperatorDriveClient.Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, parts) in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.GetFullPath(data.SharedRoot).Equals(sharedRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The shared library changed while publishing spare parts.");
            var entry = state.Units[item.Unit.Id];
            if (parts.Length == 0 && entry.SpareParts is null) continue;
            var report = OperatorSpareParts.ForUnit(parts, plant, scan);
            string fingerprint = report.Fingerprint(item.Label);
            if (entry.SparePartsError is null && entry.SparePartsFingerprint == fingerprint) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var requestToken = timeout.Token;
                root ??= await drive.GetAsync(boundRoot.Id, requestToken);
                if (!assets.TryGetValue(item.Gate.Id, out var asset))
                {
                    if (state.Assets.GetValueOrDefault(item.Gate.Id) is not { Pending: false } binding)
                        throw new IOException("Publish the asset's Drive folder before adding spare parts.");
                    asset = await drive.GetAsync(binding.Id, requestToken);
                    if (root.Trashed || root.MimeType != OperatorDriveClient.FolderMime || !IsChildFolder(asset, root))
                        throw new IOException("Restore the asset folder in the publishing folder before updating spare parts.");
                    assets.Add(item.Gate.Id, asset);
                }
                var unit = await drive.GetAsync(entry.Folder!.Id, requestToken);
                if (!IsChildFolder(unit, asset))
                    throw new IOException("Restore the unit folder under its asset before updating spare parts.");
                var pdf = OperatorPublishingPdf.SpareParts(item, report, OperatorDriveClient.FolderLink(unit));
                await PublishPdfAsync(unit, OperatorSpareParts.FileName, pdf, entry.SpareParts,
                    record => { entry.SpareParts = record; state.Save(sharedRoot); }, requestToken);
                entry.SparePartsFingerprint = fingerprint;
                entry.SparePartsError = null;
                state.Save(sharedRoot);
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is not DriveAuthenticationException and not OutOfMemoryException)
            {
                string message = error is OperationCanceledException ? "The spare-parts upload timed out." : error.Message;
                if (entry.SparePartsError == message) continue;
                entry.SparePartsError = message;
                state.Save(sharedRoot);
            }
        }

        void SetCatalogError(string? message)
        {
            if (state.SparePartsCatalogError == message) return;
            state.SparePartsCatalogError = message;
            state.Save(sharedRoot);
        }
    }

    private static bool IsChildFolder(OperatorDriveClient.Item folder, OperatorDriveClient.Item parent) =>
        !folder.Trashed && folder.MimeType == OperatorDriveClient.FolderMime && folder.DriveId == parent.DriveId &&
        folder.Parents?.Contains(parent.Id) == true;

    internal async Task<OperatorPublishSummary> RunAsync(DataService data, string sharedRoot, OperatorPublishingState state,
        bool force, IProgress<OperatorPublishProgress>? progress, CancellationToken token)
    {
        var units = OperatorPublicationSources.ListUnits(data)
            .Where(u => state.PublishAll || state.Units.TryGetValue(u.Unit.Id, out var entry) && !entry.Paused)
            .ToArray();
        IReadOnlyList<ResourceDefinition> resources = data.LoadResources();
        OperatorDriveClient.Item? root = null;
        var assetFolders = new Dictionary<string, OperatorDriveClient.Item>(StringComparer.OrdinalIgnoreCase);
        int current = 0, published = 0, issues = 0;
        for (int i = 0; i < units.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.GetFullPath(data.SharedRoot).Equals(sharedRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The shared library changed. Publishing will resume when the library is available.");
            var item = units[i];
            progress?.Report(new(i, units.Length, item.Label));
            if (!state.Units.TryGetValue(item.Unit.Id, out var entry))
            {
                entry = new() { GateId = item.Gate.Id };
                state.Units.Add(item.Unit.Id, entry);
            }

            try
            {
                if (entry.GateId != item.Gate.Id)
                    throw new InvalidOperationException("This unit has a published folder under a different asset. Its existing QR link was preserved.");
                OperatorPublicationSources? sources = null;
                byte[]? electrical = null;
                IReadOnlyList<GuidePdfSource> guideSources = [];
                Exception? sourceProblem = null;
                // Capture the same committed revision of each source before releasing the engineering-library lock.
                try
                {
                    using var library = data.AcquireAssetWriteLock();
                    sources = OperatorPublicationSources.Read(data, item, resources, entry, token);
                    if (!force && entry.Error is null && entry.Fingerprint == sources.Fingerprint)
                    {
                        current++;
                        continue;
                    }
                    if (sources.Primary is not null) electrical = OperatorPublicationSources.ReadPdf(sources.Primary.Path);
                    guideSources = ReadGuideSources(sources, electrical, token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { sourceProblem = error; }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var driveToken = timeout.Token;
                root ??= await RootAsync(state, sharedRoot, driveToken);
                if (!assetFolders.TryGetValue(item.Gate.Id, out var asset))
                {
                    asset = await AssetAsync(state, entry, item, root, sharedRoot, driveToken);
                    assetFolders.Add(item.Gate.Id, asset);
                }
                var folder = await EnsureFolderAsync(asset, entry.OriginalUnitName ?? item.UnitFolder, entry.Folder,
                    reserved =>
                    {
                        if (state.Units.Values.Any(other => other != entry && other.Folder?.Id == reserved.Id))
                            throw new InvalidOperationException("Two units resolve to the same Drive folder.");
                        entry.Folder = reserved;
                        state.Save(sharedRoot);
                    }, driveToken);

                string folderLink = OperatorDriveClient.FolderLink(folder);
                if (electrical is not null)
                {
                    entry.PrimaryPdf = Path.GetRelativePath(sharedRoot, sources!.Primary!.Path).Replace(Path.DirectorySeparatorChar, '/');
                    entry.DrawingPairId = sources.Primary.PairId;
                    await PublishPdfAsync(folder, entry.ImportedPilot ? Path.GetFileName(sources.Primary.Path) : ElectricalName,
                        electrical, entry.Electrical, record => { entry.Electrical = record; state.Save(sharedRoot); }, driveToken);
                }

                if (sourceProblem is not null) throw sourceProblem;
                if (sources!.PrimaryReassigned && sources.Primary is null && entry.Electrical is not null)
                {
                    var notice = OperatorPublishingPdf.NoAssignedDrawing(item, folderLink);
                    await PublishPdfAsync(folder, ElectricalName, notice, entry.Electrical,
                        record => { entry.Electrical = record; state.Save(sharedRoot); }, driveToken);
                    entry.PrimaryPdf = null;
                    entry.DrawingPairId = null;
                }
                var notes = await drive.NotesAsync(folder, entry.NotesId, driveToken);
                entry.NotesId = notes.Id;
                entry.NotesLink = notes.WebViewLink ?? $"https://docs.google.com/document/d/{notes.Id}/edit";
                state.Save(sharedRoot);

                var photo = sources!.PhotoPath is null ? null : await loadPhoto(sources.PhotoPath, driveToken);
                var guide = OperatorPublishingPdf.Guide(item, photo, guideSources, folderLink,
                    sources.Primary is null ? null : entry.Electrical?.Link, entry.NotesLink);
                await PublishPdfAsync(folder, GuideName, guide, entry.Guide,
                    record => { entry.Guide = record; state.Save(sharedRoot); }, driveToken);

                entry.Fingerprint = sources.Fingerprint;
                entry.Error = null;
                state.Save(sharedRoot);
                published++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                issues++;
                string message = error is OperationCanceledException ? "The Drive request timed out." : error.Message;
                if (entry.Error != message)
                {
                    entry.Error = message;
                    state.Save(sharedRoot);
                }
                // Account or parent-folder failures are common to the entire library.
                if (error is DriveAuthenticationException || root is null && (error is System.Net.Http.HttpRequestException or OperationCanceledException))
                    throw;
            }
        }
        progress?.Report(new(units.Length, units.Length, ""));
        return new(published, current, issues);
    }

    private async Task<OperatorDriveClient.Item> RootAsync(OperatorPublishingState state, string sharedRoot, CancellationToken token)
    {
        if (state.Root is { Pending: false } root)
        {
            var existing = await drive.GetAsync(root.Id, token);
            if (existing.Trashed || existing.MimeType != OperatorDriveClient.FolderMime)
                throw new IOException("The publishing folder is unavailable in Google Drive.");
            SetRoot(new(existing.Id, OperatorDriveClient.FolderLink(existing)));
            return existing;
        }

        var myDrive = await drive.GetAsync("root", token);
        return await EnsureFolderAsync(myDrive, OperatorPublishingState.DefaultFolderName, state.Root, SetRoot, token);

        void SetRoot(DriveFolder value)
        {
            if (state.Root == value) return;
            state.Root = value;
            state.Save(sharedRoot);
        }
    }

    private async Task<OperatorDriveClient.Item> AssetAsync(OperatorPublishingState state, OperatorUnitPublication entry,
        OperatorPublicationUnit item, OperatorDriveClient.Item root, string sharedRoot, CancellationToken token)
    {
        state.Assets.TryGetValue(item.Gate.Id, out var bound);
        if (bound is null)
        {
            var migrated = state.Units.Values.FirstOrDefault(u => u.GateId == item.Gate.Id && u.ImportedPilot && u.Folder is not null);
            if (migrated?.Folder is { Pending: false } pilotFolder)
            {
                var unit = await drive.GetAsync(pilotFolder.Id, token);
                if (unit.Parents?.Length != 1 || unit.Trashed || unit.MimeType != OperatorDriveClient.FolderMime)
                    throw new IOException("Restore the original pilot unit folder to preserve its QR link.");
                bound = new(unit.Parents[0]);
            }
        }
        return await EnsureFolderAsync(root, entry.OriginalAssetName ?? item.AssetFolder, bound, reserved =>
        {
            if (state.Assets.Any(asset => asset.Key != item.Gate.Id && asset.Value.Id == reserved.Id))
                throw new InvalidOperationException("Two assets resolve to the same Drive folder.");
            state.Assets[item.Gate.Id] = reserved;
            state.Save(sharedRoot);
        }, token);
    }

    private async Task<OperatorDriveClient.Item> EnsureFolderAsync(OperatorDriveClient.Item parent, string name,
        DriveFolder? bound, Action<DriveFolder> save, CancellationToken token)
    {
        var folder = bound switch
        {
            null => await drive.FindAsync(parent, name, token),
            { Pending: true } => await drive.GetPendingAsync(bound.Id, token),
            _ => await drive.GetAsync(bound.Id, token)
        };
        if (folder is null)
        {
            string id = bound?.Id ?? await drive.GenerateIdAsync(token);
            save(new(id, Pending: true));
            folder = await drive.CreateFolderAsync(id, parent, name, token);
            if (folder.Id != id) throw new IOException("Drive returned an unexpected folder ID.");
        }
        if (folder.Trashed || folder.MimeType != OperatorDriveClient.FolderMime || folder.DriveId != parent.DriveId ||
            folder.Parents?.Contains(parent.Id) != true)
            throw new IOException($"Restore the original {name} folder to its publishing location.");
        var resolved = new DriveFolder(folder.Id, OperatorDriveClient.FolderLink(folder));
        if (resolved != bound) save(resolved);
        return folder;
    }

    private async Task PublishPdfAsync(OperatorDriveClient.Item folder, string name, byte[] bytes, DrivePdf? record,
        Action<DrivePdf> save, CancellationToken token)
    {
        if (bytes.Length > OperatorPublicationSources.MaximumPdfBytes)
            throw new IOException($"{name} exceeds the 80 MiB publishing limit.");
        string checksum = Convert.ToHexStringLower(MD5.HashData(bytes));
        var remote = record switch
        {
            null => await drive.FindAsync(folder, name, token),
            { Checksum: null, PendingChecksum: not null } => await drive.GetPendingAsync(record.Id, token),
            _ => await drive.GetAsync(record.Id, token)
        };
        if (remote is null)
        {
            string id = record?.Id ?? await drive.GenerateIdAsync(token);
            record = new(id, PendingChecksum: checksum);
            save(record);
            remote = await drive.CreatePdfAsync(id, folder.Id, name, bytes, token);
            if (remote.Id != id || remote.Md5Checksum != checksum)
                throw new IOException($"Drive did not confirm the uploaded {name}.");
        }

        if (remote.Trashed || remote.MimeType != "application/pdf" || remote.DriveId != folder.DriveId ||
            remote.Parents?.Contains(folder.Id) != true || remote.Md5Checksum is null)
            throw new IOException($"Restore the original {name} in its unit folder to continue publishing.");

        if (remote.Md5Checksum != checksum)
        {
            if (remote.Md5Checksum != record?.Checksum && remote.Md5Checksum != record?.PendingChecksum)
                throw new IOException($"{name} was changed in Drive. Restore the last published revision there to resume automatic updates.");
            await drive.CheckUnchangedAsync(remote, token);
            save(new(remote.Id, remote.Md5Checksum, checksum, OperatorDriveClient.PdfLink(remote)));
            remote = await drive.UpdatePdfAsync(remote.Id, bytes, remote.ETag, token);
            if (remote.Md5Checksum != checksum)
                throw new IOException($"Drive did not confirm the updated {name}.");
        }

        var completed = new DrivePdf(remote.Id, checksum, Link: OperatorDriveClient.PdfLink(remote));
        if (completed != record) save(completed);
    }

    private static IReadOnlyList<GuidePdfSource> ReadGuideSources(OperatorPublicationSources sources,
        byte[]? electrical, CancellationToken token)
    {
        var result = new List<GuidePdfSource>(sources.Sections.Count);
        long total = 0;
        foreach (var section in sources.Sections)
        {
            token.ThrowIfCancellationRequested();
            var bytes = result.Count == 0 && electrical is not null ? electrical : OperatorPublicationSources.ReadPdf(section.Path);
            total += bytes.Length;
            if (total > OperatorPublicationSources.MaximumPdfBytes)
                throw new IOException("The guide sources exceed 80 MiB. Reduce the size of the additional PDFs.");
            result.Add(new(section.Title, bytes));
        }
        return result;
    }
}
