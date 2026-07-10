using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hi3Helper.Plugin.Wuwa.Management;

/// <summary>
/// Builds candidate CDN manifest/download paths for an installed game version when
/// persisted URLs are unavailable (e.g. API has moved ahead).
/// </summary>
internal static class WuwaVersionResourceUrlResolver
{
    // CDN layout: .../50004/{gameVersion}/{hashToken}/resource/... or .../zip
    private static readonly Regex ResourcePathTokenRegex = new(
        @"50004/([\d.]+)/([^/]+)/(?:resource|zip)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal readonly record struct Candidate(string IndexPath, string[] ZipBasePaths);

    /// <summary>
    /// Collects CDN hash tokens from API paths. Only accepts hash-like tokens (not
    /// launcher semver segments such as 1.0.0 or 2.0.0 that appear in legacy paths).
    /// </summary>
    internal static List<string> CollectCdnPathTokens(
        GameVersion installedVersion,
        WuwaApiResponseGameConfig? apiConfig,
        WuwaApiResponseGameConfigRef? activePatchConfig)
    {
        string installed = installedVersion.ToString();
        var versionMatched = new List<string>();
        var other = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddToken(string? token, bool prefer)
        {
            if (!IsPlausibleCdnHashToken(token) || !seen.Add(token!))
                return;

            if (prefer)
                versionMatched.Add(token!);
            else
                other.Add(token!);
        }

        void Scan(string? path, bool preferWhenVersionMatches)
        {
            if (string.IsNullOrEmpty(path))
                return;

            // Historical full-index paths: .../50004/3.4.1/{hash}/resource/50004/3.4.1/...
            var historical = new Regex(
                $@"50004/{Regex.Escape(installed)}/([^/]+)/resource/50004/{Regex.Escape(installed)}",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            foreach (Match match in historical.Matches(path))
                AddToken(match.Groups[1].Value, prefer: true);

            foreach (Match match in ResourcePathTokenRegex.Matches(path))
            {
                string pathVersion = match.Groups[1].Value;
                string token = match.Groups[2].Value;
                bool prefer = preferWhenVersionMatches
                    || string.Equals(pathVersion, installed, StringComparison.OrdinalIgnoreCase);
                AddToken(token, prefer);
            }
        }

        // Highest priority: paths tied to the installed version in the active patch config.
        if (activePatchConfig != null)
        {
            Scan(activePatchConfig.IndexFile, preferWhenVersionMatches: true);
            Scan(activePatchConfig.BaseUrl, preferWhenVersionMatches: true);
        }

        if (apiConfig?.Default?.ConfigReference is { } defaultRef)
        {
            Scan(defaultRef.IndexFile, preferWhenVersionMatches: false);
            Scan(defaultRef.BaseUrl, preferWhenVersionMatches: false);

            if (defaultRef.PatchConfig != null)
            {
                foreach (var patch in defaultRef.PatchConfig)
                {
                    bool prefer = patch.CurrentVersion == installedVersion;
                    Scan(patch.IndexFile, prefer);
                    Scan(patch.BaseUrl, prefer);
                }
            }
        }

        if (apiConfig?.PredownloadReference?.ConfigReference is { } preloadRef)
        {
            Scan(preloadRef.IndexFile, preferWhenVersionMatches: false);
            Scan(preloadRef.BaseUrl, preferWhenVersionMatches: false);
            ScanPatchConfigs(preloadRef.PatchConfig);
        }

        var ordered = new List<string>(versionMatched.Count + other.Count);
        ordered.AddRange(versionMatched);
        ordered.AddRange(other);

        if (ordered.Count > 0)
        {
            SharedStatic.InstanceLogger.LogDebug(
                "[WuwaVersionResourceUrlResolver] CDN hash tokens for {Version}: {Tokens}",
                installed, string.Join(", ", ordered));
        }

        return ordered;

        void ScanPatchConfigs(WuwaApiResponseGameConfigRef[]? patchConfigs)
        {
            if (patchConfigs == null)
                return;

            foreach (var patch in patchConfigs)
            {
                bool prefer = patch.CurrentVersion == installedVersion;
                Scan(patch.IndexFile, prefer);
                Scan(patch.BaseUrl, prefer);
            }
        }
    }

    internal static bool IsPlausibleCdnHashToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 16)
            return false;

        // Legacy launcher paths embed semver folders (1.0.0, 2.0.1, …) — not CDN hashes.
        if (GameVersion.TryParse(token, null, out _))
            return false;

        foreach (char c in token)
        {
            if (!char.IsLetterOrDigit(c))
                return false;
        }

        return true;
    }

    internal static IEnumerable<Candidate> BuildProbeCandidates(
        GameVersion installedVersion,
        WuwaApiResponseGameConfig? apiConfig,
        WuwaApiResponseGameConfigRef? activePatchConfig)
    {
        if (installedVersion == GameVersion.Empty)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Priority 1: exact API paths from patchConfig entries for the installed version.
        // Modern layouts nest source manifests under the live patch tree, e.g.
        // .../50004/3.5.0/{hash}/resource/50004/3.5.0/3.4.1/indexFile.json
        foreach (WuwaApiResponseGameConfigRef patch in EnumerateMatchingPatchConfigs(
                     installedVersion, apiConfig, activePatchConfig))
        {
            if (string.IsNullOrEmpty(patch.IndexFile))
                continue;

            string indexPath = patch.IndexFile.TrimStart('/');
            if (!seen.Add(indexPath))
                continue;

            SharedStatic.InstanceLogger.LogDebug(
                "[WuwaVersionResourceUrlResolver] Direct patchConfig candidate for {Version}: index={Index}",
                installedVersion, indexPath);

            yield return new Candidate(
                indexPath,
                BuildDownloadBasePathsForPatchConfig(patch, apiConfig));
        }

        // Priority 2: legacy full-index layout .../50004/{version}/{hash}/resource/50004/{version}/
        string version = installedVersion.ToString();
        foreach (string token in CollectCdnPathTokens(installedVersion, apiConfig, activePatchConfig))
        {
            string indexPath =
                $"launcher/game/G153/50004/{version}/{token}/resource/50004/{version}/indexFile.json";
            if (!seen.Add(indexPath))
                continue;

            yield return new Candidate(indexPath, BuildZipBasePaths(version, token, apiConfig));
        }
    }

    /// <summary>
    /// Picks the download base path after a manifest is fetched. Manifest entries use
    /// <c>fromFolder</c> (zip CDN) rather than the patchConfig <c>resources/</c> path.
    /// </summary>
    internal static string? ResolveDownloadBasePath(
        WuwaApiResponseResourceIndex index,
        string[] fallbackPaths)
    {
        if (index.Resource != null)
        {
            foreach (WuwaApiResponseResourceEntry entry in index.Resource)
            {
                if (string.IsNullOrEmpty(entry.FromFolder))
                    continue;

                string fromFolder = entry.FromFolder.TrimEnd('/');
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaVersionResourceUrlResolver] Using manifest fromFolder as download base: {Base}",
                    fromFolder);
                return fromFolder;
            }
        }

        return fallbackPaths.FirstOrDefault()?.TrimStart('/').TrimEnd('/');
    }

    private static IEnumerable<WuwaApiResponseGameConfigRef> EnumerateMatchingPatchConfigs(
        GameVersion installedVersion,
        WuwaApiResponseGameConfig? apiConfig,
        WuwaApiResponseGameConfigRef? activePatchConfig)
    {
        var seen = new HashSet<WuwaApiResponseGameConfigRef>();

        if (activePatchConfig != null
            && activePatchConfig.CurrentVersion == installedVersion
            && seen.Add(activePatchConfig))
        {
            yield return activePatchConfig;
        }

        if (apiConfig?.Default?.ConfigReference?.PatchConfig != null)
        {
            foreach (WuwaApiResponseGameConfigRef patch in apiConfig.Default.ConfigReference.PatchConfig)
            {
                if (patch.CurrentVersion == installedVersion && seen.Add(patch))
                    yield return patch;
            }
        }

        if (apiConfig?.PredownloadReference?.ConfigReference?.PatchConfig != null)
        {
            foreach (WuwaApiResponseGameConfigRef patch in apiConfig.PredownloadReference.ConfigReference.PatchConfig)
            {
                if (patch.CurrentVersion == installedVersion && seen.Add(patch))
                    yield return patch;
            }
        }
    }

    private static string[] BuildDownloadBasePathsForPatchConfig(
        WuwaApiResponseGameConfigRef patch,
        WuwaApiResponseGameConfig? apiConfig)
    {
        var paths = new List<string>();

        // Live target zip CDN — source manifests reference this via fromFolder.
        if (apiConfig?.Default?.ConfigReference?.BaseUrl is { Length: > 0 } liveBase)
            paths.Add(liveBase.TrimEnd('/'));

        if (apiConfig?.PredownloadReference?.ConfigReference?.BaseUrl is { Length: > 0 } preloadBase)
            paths.Add(preloadBase.TrimEnd('/'));

        string? token = TryExtractHashToken(patch.IndexFile);
        if (token != null && patch.CurrentVersion != GameVersion.Empty)
        {
            paths.AddRange(BuildZipBasePaths(patch.CurrentVersion.ToString(), token, apiConfig));
        }

        // patch.BaseUrl points at a resources/ tree that does not host downloadable files.
        return paths
            .Where(static p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? TryExtractHashToken(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (Match match in ResourcePathTokenRegex.Matches(path))
        {
            string token = match.Groups[2].Value;
            if (IsPlausibleCdnHashToken(token))
                return token;
        }

        return null;
    }

    private static string[] BuildZipBasePaths(
        string installedVersion,
        string token,
        WuwaApiResponseGameConfig? apiConfig)
    {
        var paths = new List<string>
        {
            $"launcher/game/G153/50004/{installedVersion}/{token}/zip"
        };

        if (GameVersion.TryParse(installedVersion, null, out GameVersion parsed))
        {
            // Mirror live API behaviour where zip folder version can trail index version
            // (e.g. index 3.5.0, zip 3.5.1).
            string bumped = new GameVersion(parsed.Major, parsed.Minor, parsed.Build + 1, parsed.Revision)
                .ToString();
            if (!string.Equals(bumped, installedVersion, StringComparison.Ordinal))
            {
                paths.Add($"launcher/game/G153/50004/{bumped}/{token}/zip");
            }
        }

        if (apiConfig?.Default?.ConfigReference is { IndexFile: not null, BaseUrl: not null } liveRef
            && TryExtractVersionFolder(liveRef.IndexFile, out string? liveIndexVersion)
            && TryExtractVersionFolder(liveRef.BaseUrl, out string? liveZipVersion)
            && !string.Equals(liveIndexVersion, liveZipVersion, StringComparison.Ordinal))
        {
            paths.Add($"launcher/game/G153/50004/{liveZipVersion}/{token}/zip");
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryExtractVersionFolder(string path, out string? version)
    {
        version = null;
        Match match = Regex.Match(path, @"50004/([\d.]+)/", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        version = match.Groups[1].Value;
        return true;
    }
}
