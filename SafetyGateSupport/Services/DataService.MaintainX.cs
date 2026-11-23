using System.IO;
using System.Text.Json;

namespace SafetyGateSupport.Services;

public partial class DataService
{
    private string MaintainXLinksFile => Path.Combine(SharedRoot, "Data", "maintainx-assets.json");

    internal MaintainXAsset? LoadMaintainXLink(string unitId) => ReadMaintainXLinks().GetValueOrDefault(unitId);

    internal void SaveMaintainXLink(string unitId, MaintainXAsset? previous, MaintainXAsset link)
    {
        using var lease = AcquireAssetWriteLock();
        if (!LoadSystems().Any(unit => unit.Id == unitId))
            throw new InvalidOperationException("This unit no longer exists. Reopen the unit library.");

        var links = ReadMaintainXLinks();
        if (links.GetValueOrDefault(unitId) == link) return;
        if (links.GetValueOrDefault(unitId) != previous)
            throw new InvalidOperationException("Someone changed this unit’s MaintainX link. Close and reopen work orders before editing it.");

        links[unitId] = link;
        SaveJsonWithBackup(MaintainXLinksFile, links);
    }

    private Dictionary<string, MaintainXAsset> ReadMaintainXLinks()
    {
        try
        {
            using var stream = new FileStream(MaintainXLinksFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, MaintainXAsset>>(stream, _read)
                ?? throw new IOException("The shared MaintainX links could not be read.");
        }
        catch (FileNotFoundException) { return new(); }
    }
}
