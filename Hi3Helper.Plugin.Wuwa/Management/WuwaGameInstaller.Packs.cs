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
    private bool _verifiedResourceIndex;

    private WuwaGameInstaller(WuwaGameManager manager, HttpClient client) : base(manager)
    {
        _downloadHttpClient = client;
        _ownsDownloadClient = false;
    }

    private string DownloadCachePath(string installPath, bool patch) => _operationCachePath ??
        Path.Combine(installPath, "TempPath", patch ? PatchTempDirName : "TempGameFiles");

    private WuwaGameInstaller CreatePackInstaller(WuwaGameManager manager, string name,
        WuwaApiResponseGameConfigRef target, bool preload, GameVersion currentVersion)
    {
        var context = manager.CreateOperationContext(target, currentVersion, preload);
        return new WuwaGameInstaller(context, _downloadHttpClient)
        {
            _operationCachePath = Path.Combine(EnsureAndGetGamePath(), "launcherDownload", name, target.CurrentVersion.ToString())
        };
    }

    private static GameVersion? GetLegacyPackSourceVersion(WuwaGameManager manager,
        KeyValuePair<string, WuwaApiResponseGameConfigRef>[] packs) =>
        manager.TryGetLegacyResourcePackVersion(out var version, out _) &&
        packs.All(pack => pack.Value.PatchConfig?.Any(patch => patch.CurrentVersion == version) == true)
            ? version : null;

    private async Task<long> GetResourcePackSizeAsync(GameInstallerKind kind, bool downloaded, CancellationToken token)
    {
        if (kind == GameInstallerKind.None) return 0;
        var manager = (WuwaGameManager)GameManager;
        if (kind == GameInstallerKind.Update)
            await manager.TryReconcileLegacyResourcePacksAsync(this, token).ConfigureAwait(false);
        var packs = manager.GetResourcePacks(kind == GameInstallerKind.Preload);
        // Collapse asks for these sizes before showing the preload button. Plan from
        // saved metadata here; verify the installed sources only when starting work.
        var legacyVersion = kind == GameInstallerKind.Preload ? GetLegacyPackSourceVersion(manager, packs) : null;
        long total = 0;
        foreach (var pack in packs)
        {
            GameVersion currentVersion = legacyVersion ?? manager.ReadPackVersion(pack.Key);
            if (kind != GameInstallerKind.Install && currentVersion == pack.Value.CurrentVersion)
                continue;
            var installer = CreatePackInstaller(manager, pack.Key, pack.Value, kind == GameInstallerKind.Preload, currentVersion);
            try
            {
                var patch = kind == GameInstallerKind.Install ? null :
                    pack.Value.PatchConfig?.FirstOrDefault(p => p.CurrentVersion == currentVersion);
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
                    token.ThrowIfCancellationRequested();
                    var file = new FileInfo(path);
                    // Status reports downloaded bytes. Content is verified when
                    // downloading/resuming or applying, not when opening the box.
                    bool present = kind == GameInstallerKind.Preload
                        ? file.Exists && file.Length == size
                        : await WuwaFileIntegrity.MatchesAsync(path, entry.Size, entry.Md5, token).ConfigureAwait(false);
                    if (present)
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
        if (kind == GameInstallerKind.Update)
            await manager.TryReconcileLegacyResourcePacksAsync(this, token).ConfigureAwait(false);
        // Snapshot the selected targets once. Each operation context remains pinned to
        // its pack/version even if the root manager refreshes its API response later.
        var packs = manager.GetResourcePacks(kind == GameInstallerKind.Preload);
        var legacyVersion = kind == GameInstallerKind.Preload ? GetLegacyPackSourceVersion(manager, packs) : null;
        if (legacyVersion is { } sourceVersion)
        {
            progressStateDelegate?.Invoke(InstallProgressState.Verify);
            await manager.TryReconcileLegacyResourcePacksAsync(this, token).ConfigureAwait(false);
            if (packs.Any(pack => manager.ReadPackVersion(pack.Key) != sourceVersion))
                throw new InvalidDataException("The installed game could not be verified against its saved version. Repair or update the game before retrying the preload.");
        }
        string installPath = EnsureAndGetGamePath();
        var operations = new List<(string Name, WuwaApiResponseGameConfigRef Target, WuwaGameInstaller Installer,
            WuwaApiResponseResourceIndex Index)>();
        try
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs)
            {
                var installer = CreatePackInstaller(manager, pack.Key, pack.Value, kind == GameInstallerKind.Preload, manager.ReadPackVersion(pack.Key));
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
                // patch. Full installs already verified every target before committing.
                if (!installer._verifiedResourceIndex)
                {
                    var entries = operation.Index.Resource.Where(entry => !string.IsNullOrEmpty(entry.Dest)).ToArray();
                    var progress = new InstallProgress
                    {
                        TotalBytesToDownload = entries.Sum(entry => checked((long)entry.Size)),
                        TotalCountToDownload = entries.Length,
                        TotalStateToComplete = entries.Length
                    };
                    State(InstallProgressState.Verify);
                    progressDelegate?.Invoke(in progress);
                    foreach (var entry in entries)
                    {
                        if (!await WuwaFileIntegrity.MatchesAsync(Path.Combine(installPath, entry.Dest!.Replace('/', Path.DirectorySeparatorChar)),
                                entry.Size, entry.Md5, token).ConfigureAwait(false))
                        {
                            await new Install(installer).RunAsync(GameInstallerKind.Update, progressDelegate, State, token).ConfigureAwait(false);
                            break;
                        }
                        progress.DownloadedBytes += checked((long)entry.Size);
                        progress.DownloadedCount++;
                        progress.StateCount++;
                        progressDelegate?.Invoke(in progress);
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
