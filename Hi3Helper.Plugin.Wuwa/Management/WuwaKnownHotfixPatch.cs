using Hi3Helper.Plugin.Wuwa.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management;

internal sealed record WuwaHotfixPatchFile(
    string ResourceType,
    string Name,
    long Size,
    string Sha1,
    string? Md5 = null,
    string? RemoteName = null);

internal sealed record WuwaHotfixComponent(
    string ResourceType,
    string SourceVersion,
    string TargetVersion,
    string BaseUrl,
    IReadOnlyList<WuwaHotfixPatchFile> Files,
    WuwaHotfixPatchFile Manifest);

internal sealed class WuwaKnownHotfixPatch
{
    private const string SavedResourcesPath = "Client/Saved/Resources";
    private static readonly string WindowsCdnRoot = "https://" +
        "AAcNThIADwwWB04LFE4OAE0CCApOBAIOBk0NBhdMExEMB0wADwoGDRdMFBIwNC5QNBkIUxoaOwckOTYZWwQlDA0sIhdbEAgXCDRMNAoNBwwUEA"
            .AeonPlsHelpMe();
    private static readonly Regex MountVersionRegex = new(
        @"^(?:Launcher|Resource)/(?<version>\d+\.\d+\.\d+)/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    internal WuwaKnownHotfixPatch(
        string packageVersion,
        IReadOnlyList<WuwaHotfixComponent> components)
    {
        PackageVersion = packageVersion;
        Components = components;
    }

    internal string PackageVersion { get; }
    internal IReadOnlyList<WuwaHotfixComponent> Components { get; }
    internal IEnumerable<WuwaHotfixPatchFile> Files => Components.SelectMany(x => x.Files);
    internal IEnumerable<WuwaHotfixPatchFile> Manifests => Components.Select(x => x.Manifest);
    internal IEnumerable<WuwaHotfixPatchFile> Downloads => Files.Concat(Manifests);
    internal long DownloadSize => Downloads.Sum(x => x.Size);
    internal string WorkName => string.Join("_", Components.Select(
        x => $"{x.ResourceType}-{x.SourceVersion}-{x.TargetVersion}"));

    internal static async Task<WuwaKnownHotfixPatch?> DiscoverAsync(
        string gamePath,
        string packageVersion,
        HttpClient client,
        CancellationToken token)
    {
        var components = new List<WuwaHotfixComponent>(2);
        foreach (string resourceType in new[] { "Launcher", "Resource" })
        {
            string? sourceVersion = FindMountedSourceVersion(gamePath, packageVersion, resourceType);
            if (sourceVersion == null)
                continue;

            WuwaHotfixComponent? component = await DiscoverComponentAsync(
                client, resourceType, sourceVersion, token).ConfigureAwait(false);
            if (component != null)
                components.Add(component);
        }

        return components.Count == 0
            ? null
            : new WuwaKnownHotfixPatch(packageVersion, components);
    }

    internal string GetPackageRoot(string gamePath) =>
        Path.Combine(gamePath, SavedResourcesPath.Replace('/', Path.DirectorySeparatorChar), PackageVersion);

    internal string GetResourceVersionPath(string gamePath, string resourceType, string version) =>
        Path.Combine(GetPackageRoot(gamePath), resourceType, version);

    internal Uri GetDownloadUri(WuwaHotfixPatchFile file)
    {
        WuwaHotfixComponent component = Components.Single(x =>
            x.ResourceType.Equals(file.ResourceType, StringComparison.OrdinalIgnoreCase));
        return new Uri($"{component.BaseUrl}/{file.RemoteName ?? file.Name}");
    }

    internal bool IsInstalled(string gamePath) => Components.All(component =>
        MountContainsTarget(gamePath, component));

    internal bool CanApply(string gamePath)
    {
        if (Components.Count == 0 || IsInstalled(gamePath))
            return false;

        return Components.All(component =>
            Directory.Exists(GetResourceVersionPath(
                gamePath, component.ResourceType, component.SourceVersion)) &&
            !Directory.Exists(GetResourceVersionPath(
                gamePath, component.ResourceType, component.TargetVersion)));
    }

    private bool MountContainsTarget(string gamePath, WuwaHotfixComponent component)
    {
        string path = Path.Combine(
            GetPackageRoot(gamePath), "Mount", $"Mount{component.ResourceType}.txt");
        if (!File.Exists(path))
            return false;

        string expected = $"{component.ResourceType}/{component.TargetVersion}/";
        return File.ReadAllText(path).Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WuwaHotfixComponent?> DiscoverComponentAsync(
        HttpClient client,
        string resourceType,
        string sourceVersion,
        CancellationToken token)
    {
        if (!Version.TryParse(sourceVersion, out Version? source))
            return null;

        string? targetVersion = null;
        for (int increment = 1; increment <= 16; increment++)
        {
            token.ThrowIfCancellationRequested();
            string candidate = $"{source.Major}.{source.Minor}.{source.Build + increment}";
            string probeName = resourceType == "Launcher"
                ? $"ManifestLauncher_ls_{sourceVersion}_{candidate}.hp"
                : $"ManifestResource_g0_{sourceVersion}_{candidate}.hp";
            if (await TryGetMetadataAsync(
                    client, $"{WindowsCdnRoot}/{candidate}/{probeName}", token)
                .ConfigureAwait(false) != null)
            {
                targetVersion = candidate;
                break;
            }
        }

        if (targetVersion == null)
            return null;

        string baseUrl = $"{WindowsCdnRoot}/{targetVersion}";
        var files = new List<WuwaHotfixPatchFile>();
        if (resourceType == "Launcher")
        {
            string name = $"ManifestLauncher_ls_{sourceVersion}_{targetVersion}.hp";
            WuwaHotfixPatchFile? file = await CreateFileAsync(
                client, baseUrl, resourceType, name, token).ConfigureAwait(false);
            if (file == null)
                return null;
            files.Add(file);
        }
        else
        {
            for (int group = 0; group < 256; group++)
            {
                string name = $"ManifestResource_g{group}_{sourceVersion}_{targetVersion}.hp";
                WuwaHotfixPatchFile? file = await CreateFileAsync(
                    client, baseUrl, resourceType, name, token).ConfigureAwait(false);
                if (file == null)
                    break;
                files.Add(file);
            }

            string lastSegmentName = $"ManifestResource_ls_{sourceVersion}_{targetVersion}.hp";
            WuwaHotfixPatchFile? lastSegment = await CreateFileAsync(
                client, baseUrl, resourceType, lastSegmentName, token).ConfigureAwait(false);
            if (lastSegment != null)
                files.Add(lastSegment);
        }

        if (files.Count == 0)
            return null;

        string manifestRemoteName = $"Manifest{resourceType}.txt";
        WuwaHotfixPatchFile? manifest = await CreateFileAsync(
            client,
            baseUrl,
            resourceType,
            manifestRemoteName,
            token,
            $"Manifest{resourceType}_{targetVersion}.txt").ConfigureAwait(false);
        return manifest == null
            ? null
            : new WuwaHotfixComponent(
                resourceType, sourceVersion, targetVersion, baseUrl, files, manifest);
    }

    private static string? FindMountedSourceVersion(
        string gamePath,
        string packageVersion,
        string resourceType)
    {
        string packageRoot = Path.Combine(
            gamePath,
            SavedResourcesPath.Replace('/', Path.DirectorySeparatorChar),
            packageVersion);
        string mountPath = Path.Combine(packageRoot, "Mount", $"Mount{resourceType}.txt");
        return ReadMountVersions(mountPath)
            .Where(version => Directory.Exists(Path.Combine(packageRoot, resourceType, version)))
            .Select(version => (Text: version, Parsed: Version.TryParse(version, out Version? parsed)
                ? parsed
                : null))
            .Where(x => x.Parsed != null)
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.Text)
            .FirstOrDefault();
    }

    private static HashSet<string> ReadMountVersions(string path)
    {
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return MountVersionRegex.Matches(File.ReadAllText(path))
            .Select(match => match.Groups["version"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<WuwaHotfixPatchFile?> CreateFileAsync(
        HttpClient client,
        string baseUrl,
        string resourceType,
        string name,
        CancellationToken token,
        string? saveName = null)
    {
        (long Size, string Md5)? metadata = await TryGetMetadataAsync(
            client, $"{baseUrl}/{name}", token).ConfigureAwait(false);
        return metadata == null
            ? null
            : new WuwaHotfixPatchFile(
                resourceType,
                saveName ?? name,
                metadata.Value.Size,
                "",
                metadata.Value.Md5,
                saveName == null ? null : name);
    }

    private static async Task<(long Size, string Md5)?> TryGetMetadataAsync(
        HttpClient client,
        string url,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            return null;

        long? size = response.Content.Headers.ContentLength;
        string? md5 = response.Headers.TryGetValues("X-Cos-Meta-Md5", out var values)
            ? values.FirstOrDefault()
            : response.Headers.ETag?.Tag.Trim('"');
        if (size is not > 0 || md5 == null || md5.Length != 32 || !md5.All(Uri.IsHexDigit))
            return null;

        return (size.Value, md5);
    }
}
