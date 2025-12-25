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
    

    protected override GameVersion CurrentGameVersion
    {
        get
        {
            string? version = CurrentGameConfigNode.GetConfigValue<string?>("version");
            if (version == null) return GameVersion.Empty;

            if (!GameVersion.TryParse(version, null, out GameVersion currentGameVersion))
                currentGameVersion = GameVersion.Empty;

            return currentGameVersion;
        }
        set => CurrentGameConfigNode.SetConfigValue("version", value.ToString());
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

    protected override bool HasPreload => ApiPreloadGameVersion != GameVersion.Empty && !HasUpdate;
    protected override bool HasUpdate => IsInstalled && ApiGameVersion != CurrentGameVersion;

    protected override bool IsInstalled
    {
        get
        {
            string executablePath1 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                CurrentGameExecutableByPreset);
            string executablePath2 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(CurrentGameInstallPath!,
                    "Client\\Binaries\\Win64\\Client-Win64-ShippingBase.dll"));
            string executablePath3 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(CurrentGameInstallPath!,
				    "Client\\Binaries\\Win64\\Client-Win64-Shipping.exe"));
			string executablePath4 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(CurrentGameInstallPath!,
					"Client\\Binaries\\Win64\\ThirdParty\\KrPcSdk_Global\\KRSDKRes\\KRSDK.bin"));
            string executablePath5 = Path.Combine(CurrentGameInstallPath ?? string.Empty,
                Path.Combine(CurrentGameInstallPath!, "app-game-config.json"));
            return File.Exists(executablePath1) &&
                           File.Exists(executablePath2) &&
                           File.Exists(executablePath3) &&
                           File.Exists(executablePath4) &&
                           File.Exists(executablePath5);
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

        return 0;
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
                using FileStream fileStream = fileInfo.OpenRead();
#if USELIGHTWEIGHTJSONPARSER
            CurrentGameConfig = WuwaGameLauncherConfig.ParseFrom(fileStream);
#else
            CurrentGameConfigNode = JsonNode.Parse(fileStream) as JsonObject ?? new JsonObject();
#endif
                SharedStatic.InstanceLogger.LogTrace(
                    "[WuwaGameManager::LoadConfig] Loaded app-game-config.json from directory: {Dir}",
                    CurrentGameInstallPath);
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
#endif

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
            Directory.CreateDirectory(CurrentGameInstallPath);

            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

#if !USELIGHTWEIGHTJSONPARSER
            using (var fs = new FileStream(configPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(fs, writerOptions))
            {
                // JsonObject supports WriteTo(Utf8JsonWriter)
                CurrentGameConfigNode.WriteTo(writer);
                writer.Flush();
            }
            SharedStatic.InstanceLogger.LogInformation("[WuwaGameManager::SaveConfig] Wrote app-game-config.json to {Path}", configPath);
#else
            // If lightweight parser is used, add equivalent persistence here
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameManager::SaveConfig] Lightweight JSON parser enabled: persistence not implemented.");
#endif
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[WuwaGameManager::SaveConfig] Failed to write app-game-config.json: {Exception}", ex);
        }
    }

    public void LogVersions()
    {
        try
        {
            GameVersion apiVer = ApiGameVersion;
            GameVersion curVer = CurrentGameVersion;
            SharedStatic.InstanceLogger.LogInformation("[WuwaGameManager::LogVersions] ApiGameVersion={Api} CurrentGameVersion={Current}", apiVer, curVer);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[WuwaGameManager::LogVersions] Failed to log versions: {Err}", ex.Message);
        }
    }
}