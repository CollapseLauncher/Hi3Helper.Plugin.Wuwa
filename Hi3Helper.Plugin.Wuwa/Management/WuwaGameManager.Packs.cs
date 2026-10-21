using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal partial class WuwaGameManager
{
    internal bool IsOperationContext { get; private set; }
    internal bool UsesResourcePacks => ApiGameConfigResponse?.ResourcePacks is { Count: > 0 };

    internal KeyValuePair<string, WuwaApiResponseGameConfigRef>[] GetResourcePacks(bool preload)
    {
        var bundles = preload ? ApiGameConfigResponse?.PredownloadReference?.Bundles : ApiGameConfigResponse?.Bundles;
        var packs = preload ? ApiGameConfigResponse?.PredownloadReference?.ResourcePacks : ApiGameConfigResponse?.ResourcePacks;
        if (packs == null || bundles == null || !bundles.TryGetValue("default", out var bundle) || bundle.ResourcePacks.Length == 0)
            throw new InvalidDataException("Resource configuration has no default bundle.");
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
                var bundle = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["bundles"]?["default"] : null;
                return bundle == null || bundle["version"]?.GetValue<string>() != packs[0].Value.CurrentVersion.ToString() ||
                    !string.IsNullOrEmpty(bundle["state"]?.GetValue<string>()) ||
                    bundle["resourcePacks"] is not JsonArray names ||
                    !new HashSet<string>(names.Select(n => n?.GetValue<string>() ?? ""), StringComparer.Ordinal)
                        .SetEquals(packs.Select(pack => pack.Key));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return true; }
        }
    }

    private void NormalizeResourcePacks()
    {
        if (!UsesResourcePacks) return;
        ApiGameConfigResponse!.Default = new() { ConfigReference = GetResourcePacks(false)[0].Value };
        if (ApiGameConfigResponse.PredownloadReference?.ResourcePacks is { Count: > 0 })
            ApiGameConfigResponse.PredownloadReference.ConfigReference = GetResourcePacks(true)[0].Value;
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
        var bundle = bundles["default"] as JsonObject ?? new JsonObject();
        if (bundle.Parent == null) bundles["default"] = bundle;
        bundle["version"] = packs[0].Value.CurrentVersion.ToString();
        bundle["state"] = "";
        var names = new JsonArray();
        foreach (var pack in packs) names.Add(pack.Key);
        bundle["resourcePacks"] = names;
        root["version"] = packs[0].Value.CurrentVersion.ToString();
        root["state"] = "";
        root["isPreDownload"] = false;
        WriteJsonAtomic(path, root);
        CurrentGameVersion = packs[0].Value.CurrentVersion;
        SaveConfig();
    }
}
