using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal partial class WuwaGameManager
{
    internal bool IsOperationContext { get; private set; }
    internal bool UsesResourcePacks => ApiGameConfigResponse?.ResourcePacks is { Count: > 0 };
    private string ResourceBundleName { get; set; } = "default";

    internal KeyValuePair<string, WuwaApiResponseGameConfigRef>[] GetResourcePacks(bool preload)
    {
        var bundles = preload ? ApiGameConfigResponse?.PredownloadReference?.Bundles : ApiGameConfigResponse?.Bundles;
        var packs = preload ? ApiGameConfigResponse?.PredownloadReference?.ResourcePacks : ApiGameConfigResponse?.ResourcePacks;
        if (packs == null || bundles == null || !bundles.TryGetValue(ResourceBundleName, out var bundle) || bundle.ResourcePacks.Length == 0)
            throw new InvalidDataException($"Resource configuration has no {ResourceBundleName} bundle.");
        return bundle.ResourcePacks.Distinct(StringComparer.Ordinal).Select(name =>
        {
            ValidatePackName(name);
            if (!packs.TryGetValue(name, out var pack) || pack.CurrentVersion == GameVersion.Empty ||
                string.IsNullOrEmpty(pack.IndexFile) || string.IsNullOrEmpty(pack.BaseUrl))
                throw new InvalidDataException($"Invalid resource pack: {name}");
            return new KeyValuePair<string, WuwaApiResponseGameConfigRef>(name, pack);
        }).ToArray();
    }

    internal static void ValidatePackName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Invalid resource pack name: {name}");
    }

    internal GameVersion ReadPackVersion(string name)
    {
        ValidatePackName(name);
        string path = Path.Combine(CurrentGameInstallPath ?? "", "launcherDownloadConfig", name + ".json");
        try
        {
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                if (node?["packName"]?.GetValue<string>() == name &&
                    GameVersion.TryParse(node["version"]?.GetValue<string>(), null, out var version))
                    return version;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        return GameVersion.Empty;
    }

    private bool HasResourcePackUpdate
    {
        get
        {
            if (!UsesResourcePacks) return false;
            var packs = GetResourcePacks(false);
            if (packs.Any(pack => ReadPackVersion(pack.Key) != pack.Value.CurrentVersion)) return true;
            try
            {
                string path = Path.Combine(CurrentGameInstallPath ?? "", "launcherDownloadConfig.json");
                var bundle = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["bundles"]?[ResourceBundleName] : null;
                return bundle == null || bundle["version"]?.GetValue<string>() != packs[0].Value.CurrentVersion.ToString() ||
                    !string.IsNullOrEmpty(bundle["state"]?.GetValue<string>()) ||
                    bundle["resourcePacks"] is not JsonArray names ||
                    !new HashSet<string>(names.Select(n => n?.GetValue<string>() ?? ""), StringComparer.Ordinal)
                        .SetEquals(packs.Select(pack => pack.Key));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return true; }
        }
    }

    private void TrySyncInstalledResourceBundleVersion()
    {
        if (DEBUG_AllowDowngrade || !IsInstalled || CurrentGameVersion >= ApiGameVersion || HasResourcePackUpdate)
            return;

        SharedStatic.InstanceLogger.LogInformation(
            "[WuwaGameManager::TrySyncInstalledResourceBundleVersion] Installed {Bundle} bundle and resource packs are current. Syncing local version from {LocalVersion} to {ApiVersion} after an external update.",
            ResourceBundleName, CurrentGameVersion, ApiGameVersion);
        CurrentGameVersion = ApiGameVersion;
        SaveConfig();
    }

    private void NormalizeResourcePacks()
    {
        if (!UsesResourcePacks) return;
        var bundles = ApiGameConfigResponse!.Bundles;
        string path = Path.Combine(CurrentGameInstallPath ?? "", "launcherDownloadConfig.json");
        var installed = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["bundles"] as JsonObject : null;
        ResourceBundleName = installed?.Select(bundle => bundle.Key).FirstOrDefault(name => bundles?.ContainsKey(name) == true)
            ?? (bundles?.ContainsKey("default") == true ? "default" : "HD");
        ApiGameConfigResponse!.Default = new() { ConfigReference = GetResourcePacks(false)[0].Value };
        if (ApiGameConfigResponse.PredownloadReference?.ResourcePacks is { Count: > 0 })
            ApiGameConfigResponse.PredownloadReference.ConfigReference = GetResourcePacks(true)[0].Value;
    }

    internal bool TryGetLegacyResourcePackVersion(out GameVersion version, out VersionResourceUrls urls)
    {
        GameVersion candidate = CurrentGameVersion;
        version = candidate;
        urls = default;
        // The legacy global manifest describes the complete HD install, not SD/UHD.
        if (DEBUG_AllowDowngrade || ResourceBundleName != "HD" || candidate == GameVersion.Empty ||
            !TryGetInstalledVersionResourceUrls(out urls) ||
            !urls.IndexUrl.EndsWith($"/resource/50004/{candidate}/indexFile.json", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(GetManifestMd5(urls.IndexUrl))) return false;

        var packs = GetResourcePacks(false);
        return new HashSet<string>(packs.Select(pack => pack.Key), StringComparer.Ordinal).SetEquals(["common", "hd"]) &&
            !packs.Any(pack => ReadPackVersion(pack.Key) > candidate) &&
            packs.Any(pack => ReadPackVersion(pack.Key) != candidate);
    }

    internal async Task TryReconcileLegacyResourcePacksAsync(WuwaGameInstaller installer, CancellationToken token)
    {
        if (!TryGetLegacyResourcePackVersion(out var version, out var urls)) return;
        string? installPath = CurrentGameInstallPath;
        var packs = GetResourcePacks(false);

        try
        {
            string path = Path.Combine(installPath!, "launcherDownloadConfig.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (root["bundles"]?[ResourceBundleName] is not JsonObject bundle) return;

            var index = await installer.FetchResourceIndexAsync(urls.IndexUrl, token).ConfigureAwait(false);
            if (index?.Resource is not { Length: > 0 } ||
                index.Resource.Any(entry => string.IsNullOrEmpty(entry.Dest) || string.IsNullOrEmpty(entry.Md5))) return;

            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameManager::TryReconcileLegacyResourcePacksAsync] Verifying {Count} installed files against legacy version {Version} before correcting pack records.",
                index.Resource.Length, version);
            foreach (var entry in index.Resource)
            {
                if (await WuwaFileIntegrity.MatchesAsync(Path.Combine(installPath!, entry.Dest!.Replace('/', Path.DirectorySeparatorChar)),
                        entry.Size, entry.Md5, token).ConfigureAwait(false)) continue;
                SharedStatic.InstanceLogger.LogWarning(
                    "[WuwaGameManager::TryReconcileLegacyResourcePacksAsync] Installed file differs from legacy manifest: {File}. Keeping pack records unchanged.", entry.Dest);
                return;
            }

            token.ThrowIfCancellationRequested();
            if (CurrentGameVersion != version || CurrentGameInstallPath != installPath) return;
            foreach (var pack in packs) WritePackVersion(pack.Key, version);
            bundle["version"] = version.ToString();
            WriteJsonAtomic(path, root);
            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameManager::TryReconcileLegacyResourcePacksAsync] Verified legacy install. Corrected Common/HD pack records to {Version}.", version);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::TryReconcileLegacyResourcePacksAsync] Could not verify legacy install; retaining existing pack versions: {Error}", ex.Message);
        }
    }

    internal WuwaGameManager CreateOperationContext(WuwaApiResponseGameConfigRef target, GameVersion currentVersion, bool preload)
    {
        var cdns = preload ? ApiGameConfigResponse?.PredownloadReference?.CdnList ?? ApiGameConfigResponse?.CdnList
            : ApiGameConfigResponse?.CdnList;
        string cdn = cdns?.FirstOrDefault(c => Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http")?.Url ?? ApiResponseAssetUrl;
        var context = new WuwaGameManager(CurrentGameExecutableByPreset, cdn, AuthenticationHash, GameTag, Hash1)
        {
            IsOperationContext = true,
            IsInitialized = true,
            ApiGameConfigResponse = new() { Default = new() { ConfigReference = target }, PredownloadReference = new() { ConfigReference = target } },
            GameResourceBaseUrl = $"{cdn.TrimEnd('/')}/{target.IndexFile?.TrimStart('/')}",
            GameResourceBasisPath = target.BaseUrl,
            CurrentGameConfigNode = (JsonObject)CurrentGameConfigNode.DeepClone(),
            DEBUG_SkipPreflight = DEBUG_SkipPreflight,
            ApiPreloadGameVersion = target.CurrentVersion
        };
        context.SetGamePath(CurrentGameInstallPath!);
        context.CurrentGameVersion = currentVersion;
        return context;
    }

    internal static void WriteJsonAtomic(string path, JsonObject node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
        using (var writer = new Utf8JsonWriter(stream)) node.WriteTo(writer);
        File.Move(tempPath, path, overwrite: true);
    }

    internal void WritePackVersion(string name, GameVersion version)
    {
        ValidatePackName(name);
        WriteJsonAtomic(Path.Combine(CurrentGameInstallPath!, "launcherDownloadConfig", name + ".json"),
            new JsonObject { ["packName"] = name, ["version"] = version.ToString() });
    }

    internal void CompleteResourceBundle(KeyValuePair<string, WuwaApiResponseGameConfigRef>[] packs)
    {
        string path = Path.Combine(CurrentGameInstallPath!, "launcherDownloadConfig.json");
        JsonObject root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new();
        var bundles = root["bundles"] as JsonObject ?? new JsonObject();
        if (bundles.Parent == null) root["bundles"] = bundles;
        var bundle = bundles[ResourceBundleName] as JsonObject ?? new JsonObject();
        if (bundle.Parent == null) bundles[ResourceBundleName] = bundle;
        bundle["version"] = packs[0].Value.CurrentVersion.ToString();
        bundle["state"] = "";
        var names = new JsonArray();
        foreach (var pack in packs) names.Add((JsonNode?)JsonValue.Create(pack.Key));
        bundle["resourcePacks"] = names;
        root["version"] = packs[0].Value.CurrentVersion.ToString();
        root["state"] = "";
        root["isPreDownload"] = false;
        WriteJsonAtomic(path, root);
        CurrentGameVersion = packs[0].Value.CurrentVersion;
        SaveConfig();
    }
}
