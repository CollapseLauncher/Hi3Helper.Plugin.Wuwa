using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal partial class WuwaGameInstaller
{
    private string? _operationCachePath;
    private HashSet<string>? _retainedPackFiles;
    private bool _ownsDownloadClient = true;

    private WuwaGameInstaller(WuwaGameManager manager, HttpClient client) : base(manager)
    {
        _downloadHttpClient = client;
        _ownsDownloadClient = false;
    }

    private string DownloadCachePath(string installPath, bool patch) => _operationCachePath ??
        Path.Combine(installPath, "TempPath", patch ? PatchTempDirName : "TempGameFiles");

    private WuwaGameInstaller CreatePackInstaller(WuwaGameManager manager, string name,
        WuwaApiResponseGameConfigRef target, bool preload)
    {
        var context = manager.CreateOperationContext(target, manager.ReadPackVersion(name), preload);
        return new WuwaGameInstaller(context, _downloadHttpClient)
        {
            _operationCachePath = Path.Combine(EnsureAndGetGamePath(), "launcherDownload", name, target.CurrentVersion.ToString())
        };
    }

    private async Task<long> GetResourcePackSizeAsync(GameInstallerKind kind, bool downloaded, CancellationToken token)
    {
        if (kind == GameInstallerKind.None) return 0;
        var manager = (WuwaGameManager)GameManager;
        long total = 0;
        foreach (var pack in manager.GetResourcePacks(kind == GameInstallerKind.Preload))
        {
            if (kind != GameInstallerKind.Install && manager.ReadPackVersion(pack.Key) == pack.Value.CurrentVersion)
                continue;
            var installer = CreatePackInstaller(manager, pack.Key, pack.Value, kind == GameInstallerKind.Preload);
            try
            {
                var patch = kind == GameInstallerKind.Install ? null :
                    pack.Value.PatchConfig?.FirstOrDefault(p => p.CurrentVersion == manager.ReadPackVersion(pack.Key));
                WuwaApiResponseResourceEntry[] entries = patch == null
                    ? (await installer.GetCachedIndexAsync(false, token).ConfigureAwait(false)
                       ?? throw new InvalidDataException($"Missing pack manifest: {pack.Key}")).Resource
                    : (await installer.DownloadPatchIndexAsync(installer.BuildPatchIndexUrl(patch)!, token).ConfigureAwait(false)
                       ?? throw new InvalidDataException($"Missing pack patch manifest: {pack.Key}")).Resource;
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Dest)) continue;
                    long size = checked((long)entry.Size);
                    if (!downloaded) { total = checked(total + size); continue; }
                    string path = Path.Combine(installer._operationCachePath!, entry.Dest.Replace('/', Path.DirectorySeparatorChar));
                    if (await WuwaFileIntegrity.MatchesAsync(path, entry.Size, entry.Md5, token).ConfigureAwait(false))
                        total = checked(total + size);
                    else if (File.Exists(path + ".tmp"))
                        total = checked(total + Math.Min(size, new FileInfo(path + ".tmp").Length));
                }
            }
            finally { installer.Dispose(); }
        }
        return total;
    }

    private async Task RunResourcePacksAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        var manager = (WuwaGameManager)GameManager;
        // Snapshot the selected targets once. Each operation context remains pinned to
        // its pack/version even if the root manager refreshes its API response later.
        var packs = manager.GetResourcePacks(kind == GameInstallerKind.Preload);
        string installPath = EnsureAndGetGamePath();
        var operations = new List<(string Name, WuwaApiResponseGameConfigRef Target, WuwaGameInstaller Installer,
            WuwaApiResponseResourceIndex Index)>();
        try
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs)
            {
                var installer = CreatePackInstaller(manager, pack.Key, pack.Value, kind == GameInstallerKind.Preload);
                var index = await installer.GetCachedIndexAsync(false, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Missing pack manifest: {pack.Key}");
                operations.Add((pack.Key, pack.Value, installer, index));
                foreach (var entry in index.Resource)
                    if (!string.IsNullOrEmpty(entry.Dest)) retained.Add(entry.Dest.Replace('\\', '/'));
            }

            foreach (var operation in operations)
            {
                token.ThrowIfCancellationRequested();
                var installer = operation.Installer;
                installer._retainedPackFiles = retained;
                // Do not expose a completed bundle until every required pack succeeded.
                void State(InstallProgressState state)
                {
                    if (state != InstallProgressState.Completed) progressStateDelegate?.Invoke(state);
                }
                bool current = manager.ReadPackVersion(operation.Name) == operation.Target.CurrentVersion;
                if (!current || kind == GameInstallerKind.Install)
                {
                    if (kind == GameInstallerKind.Install)
                        await new Install(installer).RunAsync(kind, progressDelegate, State, token).ConfigureAwait(false);
                    else
                        await new Patch(installer).RunAsync(kind, kind == GameInstallerKind.Preload,
                            progressDelegate, State, token).ConfigureAwait(false);
                }
                if (kind == GameInstallerKind.Preload) continue;
                // Check the complete origin manifest, including files omitted by the
                // patch. Repair missing/corrupt targets before certifying the pack.
                foreach (var entry in operation.Index.Resource)
                {
                    if (string.IsNullOrEmpty(entry.Dest)) continue;
                    if (!await WuwaFileIntegrity.MatchesAsync(Path.Combine(installPath, entry.Dest.Replace('/', Path.DirectorySeparatorChar)),
                            entry.Size, entry.Md5, token).ConfigureAwait(false))
                    {
                        await new Install(installer).RunAsync(GameInstallerKind.Update, progressDelegate, State, token).ConfigureAwait(false);
                        break;
                    }
                }
                manager.WritePackVersion(operation.Name, operation.Target.CurrentVersion);
            }

            if (kind != GameInstallerKind.Preload)
            {
                var merged = new Dictionary<string, WuwaApiResponseResourceEntry>(StringComparer.OrdinalIgnoreCase);
                // Keep records belonging to other bundles; selected packs replace their entries.
                var existing = WuwaGameManager.TryLoadLocalGameResourcesIndex(installPath);
                foreach (var entry in existing?.Resource ?? [])
                    if (!string.IsNullOrEmpty(entry.Dest)) merged[entry.Dest] = entry;
                foreach (var operation in operations)
                foreach (var entry in operation.Index.Resource)
                    if (!string.IsNullOrEmpty(entry.Dest)) merged[entry.Dest] = entry;
                manager.TryWriteLocalGameResources(installPath, new() { Resource = merged.Values.ToArray() });
                manager.CompleteResourceBundle(packs);
            }
            progressStateDelegate?.Invoke(InstallProgressState.Completed);
        }
        finally
        {
            foreach (var operation in operations) operation.Installer.Dispose();
        }
    }
}
