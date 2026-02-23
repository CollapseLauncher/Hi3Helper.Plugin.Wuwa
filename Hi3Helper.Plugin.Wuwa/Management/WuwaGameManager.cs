using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Core.Utility.Json;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using Hi3Helper.Plugin.Wuwa.Utils;
using Microsoft.Extensions.Logging;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypos

namespace Hi3Helper.Plugin.Wuwa.Management;

[GeneratedComClass]
internal partial class WuwaGameManager : GameManagerBase
{
    internal WuwaGameManager(string gameExecutableNameByPreset,
        string apiResponseAssetUrl,
        string authenticationHash,
        string gameTag,
        string hash1)
    {
        CurrentGameExecutableByPreset = gameExecutableNameByPreset;
        AuthenticationHash = authenticationHash;
        ApiResponseAssetUrl = apiResponseAssetUrl;
        GameTag = gameTag;
        Hash1 = hash1;
    }

    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??=
            WuwaUtils.CreateApiHttpClient(ApiResponseAssetUrl, GameTag, AuthenticationHash, "", Hash1);
        set;
    }

    [field: AllowNull, MaybeNull]
    private HttpClient ApiDownloadHttpClient
    {
        get => field ??=
            WuwaUtils.CreateApiHttpClient(ApiResponseAssetUrl, GameTag, AuthenticationHash, "", Hash1);
        set;
    }

    internal string ApiResponseAssetUrl { get; }
    private string GameTag { get; set; }
    private string AuthenticationHash { get; set; }
    private string Hash1 { get; set; }

    private WuwaApiResponseGameConfig? ApiGameConfigResponse { get; set; }
    private string CurrentGameExecutableByPreset { get; }

    private JsonObject CurrentGameConfigNode { get; set; } = new();

    internal string? GameResourceBaseUrl { get; set; }
    internal string? GameResourceBasisPath { get; set; }
    private bool IsInitialized { get; set; }

    /// <summary>
    /// When true, <see cref="CurrentGameVersion"/> returns <see cref="DEBUG_DowngradeVersionTarget"/>
    /// instead of the real on-disk version so the update/patch system treats the install as
    /// being at the downgrade target and re-applies the patch from that version.
    /// Persisted as <c>DEBUG_allowDowngrade</c> in <c>app-game-config.json</c>.
    /// The key is never written by default — add it manually to the JSON to enable.
    /// </summary>
    internal bool DEBUG_AllowDowngrade { get; set; }

    /// <summary>
    /// The version to "pretend" the game is at when <see cref="DEBUG_AllowDowngrade"/> is true.
    /// Ignored when <see cref="DEBUG_AllowDowngrade"/> is false.
    /// Persisted as <c>DEBUG_downgradeVersionTarget</c> in <c>app-game-config.json</c>.
    /// The key is never written by default — add it manually to the JSON to enable.
    /// </summary>
    internal GameVersion DEBUG_DowngradeVersionTarget { get; set; } = GameVersion.Empty;

    /// <summary>
    /// When true, the pre-flight validation (which checks whether local files already
    /// match the target version) is skipped, forcing the patch flow to always download
    /// and apply krpdiff patches. Useful for testing the actual patch-apply logic when
    /// the game is already at the target version.
    /// Persisted as <c>DEBUG_skipPreflight</c> in <c>app-game-config.json</c>.
    /// The key is never written by default — add it manually to the JSON to enable.
    /// </summary>
    internal bool DEBUG_SkipPreflight { get; set; }

    protected override GameVersion CurrentGameVersion
    {
        get
        {
            // If downgrade is enabled and the target is valid, report that version
            // so the update/patch flow treats the install as being at the older version.
            if (DEBUG_AllowDowngrade && DEBUG_DowngradeVersionTarget != GameVersion.Empty)
                return DEBUG_DowngradeVersionTarget;

#if !USELIGHTWEIGHTJSONPARSER
            string? version = CurrentGameConfigNode.GetConfigValue<string?>("version");
#else
            string? version = CurrentGameConfigNode["version"]?.GetValue<string>();
#endif
            if (version == null) return GameVersion.Empty;

            if (!GameVersion.TryParse(version, null, out GameVersion currentGameVersion))
                currentGameVersion = GameVersion.Empty;

            return currentGameVersion;
        }
        set
        {
#if !USELIGHTWEIGHTJSONPARSER
            CurrentGameConfigNode.SetConfigValue("version", value.ToString());
#else
            CurrentGameConfigNode["version"] = value.ToString();
#endif
        }
    }

    protected override GameVersion ApiGameVersion
    {
        get
        {
            if (ApiGameConfigResponse == null) return GameVersion.Empty;

            field = ApiGameConfigResponse.Default?.ConfigReference?.CurrentVersion ?? GameVersion.Empty;
            return field;
        }
        set;
    }

	/**
     * Currently, Kuro only serves their KRPDiff files on transient versions (e.g. 3.0.1)
     * and preloads. Major versions (2.8, 3.0, 3.1, etc.) have the full files, so we need to
     * 1. Check current version, if there's an update, grab the diff (can be major -> major, minor -> major, etc.)
     * 2. Parse the fromFolder JSON tag, which will tell us where to grab the new diffs from
     * 3. Parse the JSON response and only download files that end if `.krpdiff` extension
     * 3-1. krpdiff is using HDiff19 with ZSTD & Fadler64
     * 4. Once all krpdiff files are downloaded (in chunks or otherwise):
     * 4-1. Parse deleteFiles field and remove those files from the current install
     * 4-2. Parse groupInfos field and apply the krpdiff file:
     * 4-2-1. Parse `srcFiles` and get `dest` field, which is the file on which krpdiff should be ran
     * 4-2-2. Parse `dstFiles` and get `dest` field, which is what the file should be called once hdiff has ran
     * 4-2-3. As validation, we can run MD5 compute on the file before and after and compare it with MD5 hash in both JSON fields
     * 4-3. Repeat 4-2 for all `krpdiff` files.
     * 4-4. For validation, we can compute the MD5 hash and compare it with the file list in the JSON response (md5 field)
     * 5. Delete all krpdiff files and mark game as installed/updated.
     * 
     * Until the above is done, HasPreload and HasUpdate is disabled, so that the game is marked as always updated & no preloads.
     * 
     * For preloads, the procedure is similar, we can just download the KRPDiff (steps 1 to 3) files and keep them until we notice that the Preload
     * (they call it predownload) field in default -> config no longer exists, then we can notify the user and have them click the "Update Game"
     * button, which will run steps 4 & 5.
     */
	//protected override bool HasPreload => ApiPreloadGameVersion != GameVersion.Empty && !HasUpdate;
	//protected override bool HasUpdate => IsInstalled && ApiGameVersion != CurrentGameVersion;

    private bool? _lastHasUpdate;
    private bool? _lastHasPreload;

	protected override bool HasPreload
    {
        get
        {
            bool result = IsInstalled && ApiPreloadGameVersion != GameVersion.Empty && !HasUpdate;
            if (_lastHasPreload != result)
            {
                _lastHasPreload = result;
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaGameManager::HasPreload] IsInstalled={IsInstalled}, ApiPreloadGameVersion={PreloadVer}, HasUpdate={HasUpdate} => HasPreload={Result}",
                    IsInstalled, ApiPreloadGameVersion, HasUpdate, result);
            }
            return result;
        }
    }

    protected override bool HasUpdate
    {
        get
        {
            bool result = IsInstalled && (ApiGameVersion != CurrentGameVersion || HasPendingPreloadPatch);
            if (_lastHasUpdate != result)
            {
                _lastHasUpdate = result;
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaGameManager::HasUpdate] IsInstalled={IsInstalled}, ApiGameVersion={ApiVer}, CurrentGameVersion={CurVer}, VersionMismatch={Mismatch}, HasPendingPreloadPatch={Pending} => HasUpdate={Result}",
                    IsInstalled, ApiGameVersion, CurrentGameVersion, ApiGameVersion != CurrentGameVersion, HasPendingPreloadPatch, result);
            }
            return result;
        }
    }

    /// <summary>
    /// Checks if preloaded patch files exist on disk and the predownload window has closed
    /// (meaning the update has gone live and patches should be applied).
    /// </summary>
    internal bool HasPendingPreloadPatch
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath)) return false;
            string patchTempPath = Path.Combine(CurrentGameInstallPath, "TempPath", "TempPatchFiles");
            if (!Directory.Exists(patchTempPath)) return false;

            // Preload patches exist and predownload is no longer offered
            return ApiPreloadGameVersion == GameVersion.Empty;
        }
    }

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return false;
            return IsStandaloneInstall || IsSteamInstall || IsEpicInstall;
		}
    }

    protected bool IsStandaloneInstall
    {
		get
		{
			string executablePath1 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
				Path.Combine(CurrentGameInstallPath!,
					"Client\\Binaries\\Win64\\Client-Win64-ShippingBase.dll"));
			string executablePath2 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
				Path.Combine(CurrentGameInstallPath!,
					"Client\\Binaries\\Win64\\Client-Win64-Shipping.exe"));
			string executablePath3 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
				Path.Combine(CurrentGameInstallPath!,
					"Client\\Binaries\\Win64\\ThirdParty\\KrPcSdk_Global\\KRSDKRes\\KRSDK.bin"));
			string executablePath4 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
				Path.Combine(CurrentGameInstallPath!, "app-game-config.json"));
			return File.Exists(executablePath1) &&
						File.Exists(executablePath2) &&
						File.Exists(executablePath3) &&
						File.Exists(executablePath4);
		}
	}

    protected bool IsSteamInstall
    {
        get
        {
            string executablePath1 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                "Client\\Binaries\\Win64\\ThirdParty\\KrPcSdk_Global\\installscript.vdf");
            string executablePath2 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                "\\Client\\Binaries\\Win64\\AntiCheatExpert\\SGuard\\x64\\SGuard64.exe");
            string executablePath3 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                "\\Client\\Binaries\\Win64\\ThirdParty\\KrPcSdk_Global\\KRSDK.dll");

            return File.Exists(executablePath1) &&
                            File.Exists(executablePath2) &&
                            File.Exists(executablePath3);
        }
    }

    protected bool IsEpicInstall
    {
        get
		{ // This is the ONLY difference between EGS and Standalone install
			string executablePath1 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                "Client\\Binaries\\Win64\\ThirdParty\\KrPcSdk_Global\\EOSSDK-Win64-Shipping.dll");

            return File.Exists(executablePath1);
		}
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;

        using (ThisInstanceLock.EnterScope())
        {
            ApiDownloadHttpClient.Dispose();
            ApiDownloadHttpClient = null!;
            ApiGameConfigResponse = null;

            base.Dispose();
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        CurrentGameVersion = gameVersion;
    }

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = gamePath;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        return InitAsyncInner(true, token);
    }

    internal async Task<int> InitAsyncInner(bool forceInit = false, CancellationToken token = default)
    {
        if (!forceInit && IsInitialized)
            return 0;

        string gameConfigUrl =
            $"https://prod-alicdn-gamestarter.kurogame.com/launcher/game/{GameTag.AeonPlsHelpMe()}/" +
            $"{AuthenticationHash.AeonPlsHelpMe()}/index.json";

        using HttpResponseMessage configMessage =
            await ApiResponseHttpClient.GetAsync(gameConfigUrl, HttpCompletionOption.ResponseHeadersRead, token);
        configMessage.EnsureSuccessStatusCode();

        string jsonResponse = await configMessage.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("API Game Config response: {JsonResponse}", jsonResponse);
        WuwaApiResponseGameConfig? tmp = JsonSerializer.Deserialize<WuwaApiResponseGameConfig>(jsonResponse,
            WuwaApiResponseContext.Default.WuwaApiResponseGameConfig);
        ApiGameConfigResponse = tmp ?? throw new JsonException("Failed to deserialize API game config response.");

        if (ApiGameConfigResponse.Default?.ConfigReference == null)
            throw new NullReferenceException("ApiGameConfigResponse.ResponseData is null");

        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
            throw new NullReferenceException("Game API Launcher cannot retrieve CurrentVersion value!");

        // Dynamically set the resource base URL using IndexFile from the response
        GameResourceBaseUrl = $"{ApiResponseAssetUrl}{ApiGameConfigResponse.Default.ConfigReference.IndexFile}";

        // Set the basis path if needed (from BaseUrl in the response, if still required for other logic)
        GameResourceBasisPath = ApiGameConfigResponse.Default.ConfigReference.BaseUrl;
        if (GameResourceBasisPath == null)
            throw new NullReferenceException("Game API Launcher cannot retrieve BaseUrl reference value!");

        // Set API current game version dynamically
        if (ApiGameConfigResponse.Default.ConfigReference.CurrentVersion == GameVersion.Empty)
            throw new InvalidOperationException(
                $"API GameConfig returns an invalid CurrentVersion data! Data: {ApiGameConfigResponse.Default.ConfigReference.CurrentVersion}");

        ApiGameVersion = new GameVersion(ApiGameConfigResponse.Default.ConfigReference.CurrentVersion.ToString());
        IsInitialized = true;

        SharedStatic.InstanceLogger.LogInformation(
            "[WuwaGameManager::InitAsyncInner] Versions — ApiGameVersion={ApiVer}, CurrentGameVersion={CurVer}, InstallPath={Path}",
            ApiGameVersion, CurrentGameVersion, CurrentGameInstallPath ?? "(null)");

        // Set preload version from predownload config if available
        if (ApiGameConfigResponse.Default?.PredownloadReference?.CurrentVersion is { } preloadVer
            && preloadVer != GameVersion.Empty)
        {
            ApiPreloadGameVersion = preloadVer;
            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameManager::InitAsyncInner] Preload version available: {Version}", preloadVer);
        }
        else
        {
            ApiPreloadGameVersion = GameVersion.Empty;
        }

        LogGameStateOnce();
        return 0;
    }

    private void LogGameStateOnce()
    {
        string patchTempPath = string.IsNullOrEmpty(CurrentGameInstallPath)
            ? "(no install path)"
            : Path.Combine(CurrentGameInstallPath, "TempPath", "TempPatchFiles");

        SharedStatic.InstanceLogger.LogDebug(
            "[WuwaGameManager::GameState] IsInstalled={IsInstalled}, ApiGameVersion={ApiVer}, CurrentGameVersion={CurVer}, " +
            "ApiPreloadGameVersion={PreloadVer}, HasPendingPreloadPatch={PendingPatch}, PatchDir={PatchDir}, " +
            "HasUpdate={HasUpdate}, HasPreload={HasPreload}, DEBUG_AllowDowngrade={AllowDowngrade}, DEBUG_DowngradeVersionTarget={DowngradeTarget}, DEBUG_SkipPreflight={SkipPreflight}",
            IsInstalled, ApiGameVersion, CurrentGameVersion,
            ApiPreloadGameVersion, HasPendingPreloadPatch, patchTempPath,
            HasUpdate, HasPreload, DEBUG_AllowDowngrade, DEBUG_DowngradeVersionTarget, DEBUG_SkipPreflight);
    }

    protected override Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        return base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return null;

            string? rootSearchPath = Path.GetDirectoryName(Path.GetDirectoryName(CurrentGameInstallPath));
            if (string.IsNullOrEmpty(rootSearchPath))
                return null;

            string gameName = Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset);

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple
            };

#if DEBUG
        SharedStatic.InstanceLogger.LogTrace("Start finding game existing installation using prefix: {PrefixName} from root path: {RootPath}", gameName, rootSearchPath);
#endif

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(rootSearchPath, $"*{gameName}*", options))
                {
                    if (token.IsCancellationRequested)
                        return null;

#if DEBUG
                SharedStatic.InstanceLogger.LogTrace("Got executable file at: {ExecPath}", filePath);
#endif

                    string? parentPath = Path.GetDirectoryName(filePath);
                    if (parentPath == null)
                        continue;

                    string jsonPath = Path.Combine(parentPath, "app-game-config.json");
                    if (File.Exists(jsonPath))
                    {
#if DEBUG
                    SharedStatic.InstanceLogger.LogTrace("Found app-game-config.json at: {JsonPath}", jsonPath);
#endif
                        return parentPath;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }, token);


    public override void LoadConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] Game directory isn't set! Game config won't be loaded.");
            return;
        }

        string filePath = Path.Combine(CurrentGameInstallPath, "app-game-config.json");
        FileInfo fileInfo = new(filePath);

        if (fileInfo.Exists)
        {
            try
            {
                // Scope the FileStream tightly so the file handle is released before
                // TryCrossCheckKuroLauncherVersion(), which may call SaveConfig().
                using (FileStream fileStream = fileInfo.OpenRead())
                {
                    CurrentGameConfigNode = JsonNode.Parse(fileStream) as JsonObject ?? new JsonObject();
                }

                // Read DEBUG_allowDowngrade and DEBUG_downgradeVersionTarget from the config
                LoadDowngradeSettings();

                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGameManager::LoadConfig] Loaded app-game-config.json from directory: {Dir}",
                    CurrentGameInstallPath);

                // Cross-check against Kuro's launcherDownloadConfig.json to detect
                // external updates (e.g. game updated via the official Kuro launcher).
                TryCrossCheckKuroLauncherVersion();
                return;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogError(
                    "[WuwaGameManager::LoadConfig] Cannot load app-game-config.json! Reason: {Exception}", ex);
                // fallthrough: attempt recovery/write if possible
            }
        }
        else
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] File app-game-config.json doesn't exist on dir: {Dir}",
                CurrentGameInstallPath);
        }

        // If the file is missing (or failed to parse), attempt to detect an existing install by executable
        // and persist a config so manual-locate flow behaves like installer.
        try
        {
            string exePath = Path.Combine(CurrentGameInstallPath, CurrentGameExecutableByPreset);
            if (File.Exists(exePath))
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[WuwaGameManager::LoadConfig] Found executable at {Exe}. Attempting to initialize API and persist app-game-config.json.",
                    exePath);

                try
                {
                    // Ensure API metadata available for SaveConfig (best-effort; swallow errors)
                    InitAsyncInner(true, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[WuwaGameManager::LoadConfig] InitAsyncInner failed (continuing): {Err}", ex.Message);
                }

                try
                {
                    SaveConfig();
                    SharedStatic.InstanceLogger.LogInformation(
                        "[WuwaGameManager::LoadConfig] Persisted app-game-config.json for manual-located installation.");
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[WuwaGameManager::LoadConfig] Failed to persist app-game-config.json: {Err}", ex.Message);
                }
            }
            else
            {
                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGameManager::LoadConfig] No executable found at {Exe}; skipping auto-save.", exePath);
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] Recovery attempt failed: {Err}", ex.Message);
        }
    }

    /// <summary>
    /// Reads <c>DEBUG_allowDowngrade</c> and <c>DEBUG_downgradeVersionTarget</c> from
    /// <see cref="CurrentGameConfigNode"/> and populates the corresponding in-memory properties.
    /// Called once from <see cref="LoadConfig"/> after the JSON has been parsed.
    /// These keys are never written by default — they must be added manually to the JSON.
    /// </summary>
    private void LoadDowngradeSettings()
    {
#if !USELIGHTWEIGHTJSONPARSER
        DEBUG_AllowDowngrade = CurrentGameConfigNode.GetConfigValue<bool?>("DEBUG_allowDowngrade") ?? false;
        string? targetStr = CurrentGameConfigNode.GetConfigValue<string?>("DEBUG_downgradeVersionTarget");
        DEBUG_SkipPreflight = CurrentGameConfigNode.GetConfigValue<bool?>("DEBUG_skipPreflight") ?? false;
#else
        DEBUG_AllowDowngrade = CurrentGameConfigNode["DEBUG_allowDowngrade"]?.GetValue<bool>() ?? false;
        string? targetStr = CurrentGameConfigNode["DEBUG_downgradeVersionTarget"]?.GetValue<string>();
        DEBUG_SkipPreflight = CurrentGameConfigNode["DEBUG_skipPreflight"]?.GetValue<bool>() ?? false;
#endif

        if (!string.IsNullOrEmpty(targetStr) &&
            GameVersion.TryParse(targetStr, null, out GameVersion parsed) &&
            parsed != GameVersion.Empty)
        {
            DEBUG_DowngradeVersionTarget = parsed;
        }
        else
        {
            DEBUG_DowngradeVersionTarget = GameVersion.Empty;
        }

        if (DEBUG_AllowDowngrade)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadDowngradeSettings] Downgrade enabled. Target version: {Ver}",
                DEBUG_DowngradeVersionTarget);
        }

        if (DEBUG_SkipPreflight)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadDowngradeSettings] Pre-flight validation will be SKIPPED (DEBUG_skipPreflight=true).");
        }
    }

    /// <summary>
    /// Reads Kuro's <c>launcherDownloadConfig.json</c> (written by the official launcher)
    /// and updates <see cref="CurrentGameVersion"/> if the Kuro file reports a newer version
    /// than <c>app-game-config.json</c>. This handles the case where the game was updated
    /// externally (via the official Kuro launcher) without Collapse knowing about it.
    /// </summary>
    private void TryCrossCheckKuroLauncherVersion()
    {
        const string kuroConfigFileName = "launcherDownloadConfig.json";

        if (string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

        string kuroConfigPath = Path.Combine(CurrentGameInstallPath, kuroConfigFileName);
        if (!File.Exists(kuroConfigPath))
        {
            SharedStatic.InstanceLogger.LogTrace(
                "[WuwaGameManager::TryCrossCheckKuroLauncherVersion] {File} not found at {Dir}, skipping cross-check.",
                kuroConfigFileName, CurrentGameInstallPath);
            return;
        }

        try
        {
            WuwaLauncherDownloadConfig? kuroConfig;
            using (FileStream fs = File.OpenRead(kuroConfigPath))
            {
                kuroConfig = JsonSerializer.Deserialize(fs,
                    WuwaApiResponseContext.Default.WuwaLauncherDownloadConfig);
            }

            if (kuroConfig?.Version == null ||
                !GameVersion.TryParse(kuroConfig.Version, null, out GameVersion kuroVersion) ||
                kuroVersion == GameVersion.Empty)
            {
                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGameManager::TryCrossCheckKuroLauncherVersion] Could not parse version from {File}.",
                    kuroConfigFileName);
                return;
            }

            GameVersion currentVersion = CurrentGameVersion;
            if (kuroVersion > currentVersion)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[WuwaGameManager::TryCrossCheckKuroLauncherVersion] Kuro launcher reports version {KuroVer} " +
                    "which is newer than app-game-config version {CurVer}. " +
                    "Game was likely updated externally. Updating local version.",
                    kuroVersion, currentVersion);

                CurrentGameVersion = kuroVersion;
                SaveConfig();
            }
            else
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[WuwaGameManager::TryCrossCheckKuroLauncherVersion] Versions match or app-game-config is newer. " +
                    "Kuro={KuroVer}, Local={CurVer}. No action needed.",
                    kuroVersion, currentVersion);
            }
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::TryCrossCheckKuroLauncherVersion] Failed to read {File}: {Err}",
                kuroConfigFileName, ex.Message);
        }
    }

    public override void SaveConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::LoadConfig] Game directory isn't set! Game config won't be saved.");
            return;
        }

#if !USELIGHTWEIGHTJSONPARSER
        CurrentGameConfigNode.SetConfigValueIfEmpty("version", CurrentGameVersion.ToString());
        CurrentGameConfigNode.SetConfigValueIfEmpty("name",
            ApiGameConfigResponse?.KeyFileCheckList?[2] ??
            Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset));
#else
        if (!CurrentGameConfigNode.ContainsKey("version"))
            CurrentGameConfigNode["version"] = CurrentGameVersion.ToString();
        if (!CurrentGameConfigNode.ContainsKey("name"))
            CurrentGameConfigNode["name"] = ApiGameConfigResponse?.KeyFileCheckList?[2] ??
                Path.GetFileNameWithoutExtension(CurrentGameExecutableByPreset);
#endif

        string installType;
        try
        {
            if (IsStandaloneInstall) installType = "standalone";
            else if (IsSteamInstall) installType = "steam";
            else if (IsEpicInstall) installType = "epic";
            else installType = "unknown";
        } catch
        {
            installType = "unknown";
        }
        CurrentGameConfigNode["installType"] = installType;

        // Re-persist downgrade settings only if the keys already exist in the config.
        // These are debug-only keys — never written by default; the user must add them manually.
        if (CurrentGameConfigNode.ContainsKey("DEBUG_allowDowngrade"))
            CurrentGameConfigNode["DEBUG_allowDowngrade"] = DEBUG_AllowDowngrade;
        if (CurrentGameConfigNode.ContainsKey("DEBUG_downgradeVersionTarget"))
        {
            if (DEBUG_DowngradeVersionTarget != GameVersion.Empty)
                CurrentGameConfigNode["DEBUG_downgradeVersionTarget"] = DEBUG_DowngradeVersionTarget.ToString();
            else
                CurrentGameConfigNode.Remove("DEBUG_downgradeVersionTarget");
        }
        if (CurrentGameConfigNode.ContainsKey("DEBUG_skipPreflight"))
            CurrentGameConfigNode["DEBUG_skipPreflight"] = DEBUG_SkipPreflight;

        if (CurrentGameVersion == GameVersion.Empty)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::SaveConfig] Current version returns 0.0.0! Overwrite the version to current provided version by API, {VersionApi}",
                ApiGameVersion);
            CurrentGameVersion = ApiGameVersion;
        }

        // Persist to disk (write app-game-config.json)
        try
        {
            string configPath = Path.Combine(CurrentGameInstallPath, "app-game-config.json");
            string tempPath = configPath + ".tmp";
            Directory.CreateDirectory(CurrentGameInstallPath);

            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            // Write to temp file first, then atomically replace to avoid corruption.
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(fs, writerOptions))
            {
                CurrentGameConfigNode.WriteTo(writer);
                writer.Flush();
            }

            File.Move(tempPath, configPath, overwrite: true);
            SharedStatic.InstanceLogger.LogInformation(
                "[WuwaGameManager::SaveConfig] Wrote app-game-config.json to {Path}", configPath);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::SaveConfig] Failed to write app-game-config.json: {Exception}", ex);
        }
    }

    internal string GetInstallType()
    {
        try
        {
            if (IsStandaloneInstall) return "standalone";
            if (IsSteamInstall) return "steam";
            if (IsEpicInstall) return "epic";
        }
        catch
        {
            // ignore
        }
        return "unknown";
    }

    /// <summary>
    /// Finds a patch config entry that patches FROM the given version to the current API game version.
    /// </summary>
    internal WuwaApiResponseGameConfigRef? GetPatchConfigForVersion(GameVersion fromVersion)
    {
        var patchConfigs = ApiGameConfigResponse?.Default?.ConfigReference?.PatchConfig;
        if (patchConfigs == null) return null;

        foreach (var pc in patchConfigs)
        {
            if (pc.CurrentVersion == fromVersion)
                return pc;
        }
        return null;
    }

    /// <summary>
    /// Finds a preload patch config entry that patches FROM the given version to the preload version.
    /// </summary>
    internal WuwaApiResponseGameConfigRef? GetPreloadPatchConfigForVersion(GameVersion fromVersion)
    {
        var patchConfigs = ApiGameConfigResponse?.Default?.PredownloadReference?.PatchConfig;
        if (patchConfigs == null) return null;

        foreach (var pc in patchConfigs)
        {
            if (pc.CurrentVersion == fromVersion)
                return pc;
        }
        return null;
    }

    internal WuwaApiResponseGameConfigRef? ApiConfigReference
        => ApiGameConfigResponse?.Default?.ConfigReference;

    internal WuwaApiResponseGameConfigRef? ApiPredownloadReference
        => ApiGameConfigResponse?.Default?.PredownloadReference;
}