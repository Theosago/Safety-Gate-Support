using System.IO;
using SafetyGateSupport.Models;

namespace SafetyGateSupport.Services;

internal sealed record SharedNotesTransfer(string DesktopBefore, string PhoneBefore, string Result);
internal sealed record SharedNotesCheckpoint(string? CommonText, string? DriveVersion, SharedNotesTransfer? Transfer = null);

internal sealed class OperatorNotesSynchronizer(OperatorDriveClient drive, OperatorNotesClient docs)
{
    internal async Task RunAsync(DataService data, string sharedRoot, OperatorPublishingState state, CancellationToken token)
    {
        var gates = data.LoadGates();
        var targets = data.LoadSystems(gates).Where(unit => state.Units.TryGetValue(unit.Id, out var entry) &&
            entry.GateId == unit.GateId && entry.Folder is { Pending: false } && entry.NotesId is not null &&
            (state.PublishAll || !entry.Paused)).ToArray();
        if (targets.Length == 0 || state.Root is not { Pending: false } root) return;

        try
        {
            var parent = await drive.GetAsync(root.Id, token);
            var folders = targets.Select(unit => state.Units[unit.Id].Folder!.Id);
            var versions = await drive.ListNotesAsync(parent, folders, token);
            foreach (var unit in targets)
            {
                token.ThrowIfCancellationRequested();
                if (!Path.GetFullPath(data.SharedRoot).Equals(sharedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The shared library changed. Notes sync will resume when it is available.");

                var entry = state.Units[unit.Id];
                try
                {
                    if (!versions.TryGetValue(entry.NotesId!, out var remote) || remote.Trashed ||
                        remote.MimeType != OperatorDriveClient.DocMime || remote.DriveId != parent.DriveId ||
                        remote.Parents?.Contains(entry.Folder!.Id) != true)
                        throw new IOException("The linked Operator notes document is unavailable in this unit folder.");
                    if (string.IsNullOrWhiteSpace(unit.NotesFolder))
                    {
                        var gate = gates.FirstOrDefault(g => g.Id == unit.GateId)
                            ?? throw new IOException("The unit's asset is unavailable.");
                        using var lease = data.AcquireAssetWriteLock();
                        data.EnsureUnitFolders(gate, unit);
                    }
                    await SyncUnitAsync(data, sharedRoot, state, unit, entry, remote, token);
                    SetUnitError(entry, null);
                }
                catch (Exception error) when (error is not NotesApiSetupException and not DriveAuthenticationException
                    and not OperationCanceledException and not OutOfMemoryException)
                {
                    SetUnitError(entry, error.Message);
                }
            }
            SetError(null);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            SetError(error.Message);
        }

        void SetError(string? error)
        {
            if (state.NotesError == error) return;
            state.NotesError = error;
            state.Save(sharedRoot);
        }

        void SetUnitError(OperatorUnitPublication unit, string? error)
        {
            if (unit.NotesError == error) return;
            unit.NotesError = error;
            state.Save(sharedRoot);
        }
    }

    private async Task SyncUnitAsync(DataService data, string root, OperatorPublishingState state, SafetySystem unit,
        OperatorUnitPublication entry, OperatorDriveClient.Item remote, CancellationToken token)
    {
        string path = data.SystemNotesPath(unit);
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar)[0] == "..")
            throw new IOException("The shared notes file is outside the shared library.");
        string? existing = path;
        while (existing is not null && !File.Exists(existing) && !Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing);
        if (existing is not null && !Path.GetFullPath(existing).Equals(root, StringComparison.OrdinalIgnoreCase))
            OperatorPublicationSources.RequireSharedFile(root, existing);
        string original = data.LoadSystemNotes(unit);
        string desktop = Normalize(original);
        var checkpoint = entry.NotesCheckpoint;
        if (checkpoint is { Transfer: null } && checkpoint.CommonText == desktop && remote.Version is not null &&
            checkpoint.DriveVersion == remote.Version) return;

        var document = await docs.ReadAsync(remote.Id, token);
        string phone = Normalize(document.Text);
        if (desktop == phone)
        {
            Save(new(desktop, remote.Version));
            return;
        }

        var transfer = checkpoint?.Transfer;
        if (transfer is not null && (desktop != transfer.Result && desktop != transfer.DesktopBefore ||
            phone != transfer.Result && phone != transfer.PhoneBefore))
        {
            // If nobody saw the pending result, re-plan from the last confirmed common text.
            // If a side has already applied it, preserve that intermediate version for reconciliation.
            if (desktop == transfer.Result || phone == transfer.Result)
                throw new IOException("Shared notes were edited during sync. Review both copies and make their text match to resume.");
            transfer = null;
        }
        transfer ??= new(desktop, phone, Merge(checkpoint?.CommonText, desktop, phone));
        Save(new(checkpoint?.CommonText, null, transfer));
        if (phone != transfer.Result)
        {
            await docs.WriteAsync(document, transfer.Result, token);
            var confirmed = await docs.ReadAsync(remote.Id, token);
            if (Normalize(confirmed.Text) != transfer.Result)
                throw new IOException("The phone notes changed during sync. The desktop copy was kept.");
        }
        if (desktop != transfer.Result)
        {
            if (!Path.GetFullPath(data.SharedRoot).Equals(root, StringComparison.OrdinalIgnoreCase) ||
                !data.TryUpdateSystemNotes(unit, original, transfer.Result, preservePrevious: true))
                throw new IOException("The desktop notes changed during sync. The edit was kept.");
        }
        // Drive's metadata version is sampled again on the next pass after a Docs write.
        Save(new(transfer.Result, phone == transfer.Result ? remote.Version : null));

        void Save(SharedNotesCheckpoint next)
        {
            if (entry.NotesCheckpoint == next) return;
            entry.NotesCheckpoint = next;
            state.Save(root);
        }
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Merge(string? common, string desktop, string phone)
    {
        if (common is null)
        {
            if (IsAppend(desktop, phone)) return phone;
            if (IsAppend(phone, desktop)) return desktop;
            return desktop + "\n\n" + phone;
        }
        if (desktop == common) return phone;
        if (phone == common) return desktop;
        if (IsAppend(common, desktop) && IsAppend(common, phone))
        {
            string addition = phone[common.Length..];
            string separator = desktop.EndsWith('\n') || addition.StartsWith('\n') ? "" : "\n";
            return desktop + separator + addition;
        }
        throw new IOException("Shared notes were edited in both places. Review both copies and make their text match to resume.");
    }

    private static bool IsAppend(string original, string changed) => changed.StartsWith(original, StringComparison.Ordinal) &&
        (original.Length == 0 || original.EndsWith('\n') || changed.Length > original.Length && changed[original.Length] == '\n');
}
