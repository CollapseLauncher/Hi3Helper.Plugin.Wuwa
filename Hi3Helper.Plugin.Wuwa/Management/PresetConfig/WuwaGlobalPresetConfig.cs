using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Wuwa.Management.Api;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo

namespace Hi3Helper.Plugin.Wuwa.Management.PresetConfig;
[GeneratedComClass]
public partial class WuwaGlobalPresetConfig : WuwaPresetConfig
{
    private const string ApiResponseUrl = "https://prod-alicdn-gamestarter.kurogame.com/";
    private const string ApiResponseAssetUrl = "https://pcdownload-huoshan.aki-game.net/";
    private const string CurrentTag = "JFJWUA";
    private const string ClientAccess = "VlNTU1c=";
    private const string CurrentPatch = "2.7.0";
    private const string AuthenticationHash = "VlNTU1c8DAEsKzslESUCDRIQAiomLA4WKBEMIAABOQgyMSEgVAA";
    private const string Hash1 = "DSQONC4ZUxIkByA1CgANJC0OJw4lNhYMIRQHB1EtJ1s";
    private const string Hash2 = "JRUpJyABCwclGTQGMiUrOhkgOTkIIgIqBQkJMCE7JS8";
    private const string ExecutableName = "Wuthering Waves.exe";
    private const string EngineExecutableName = "Client-Win64-Shipping.exe";
	private const string VendorName = "Kuro Games";

    [field: AllowNull, MaybeNull]
    public override string GameName => field ??= "Wuthering Waves";

	[field: AllowNull, MaybeNull]
	public override string GameExecutableName => field ??= Path.Combine("Client", "Binaries", "Win64", EngineExecutableName);

    [field: AllowNull, MaybeNull]
    public override string? StartExecutableName => field ??= ExecutableName;

	public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            return string.IsNullOrEmpty(gamePath) ? string.Empty : Path.Combine(gamePath, "Client", "Saved");
        }
    }
    
    [field: AllowNull, MaybeNull]
    public override string GameLogFileName => field ??= Path.Combine("Saved", "Logs", "Client.log");

    [field: AllowNull, MaybeNull]
    public override string GameVendorName => field ??= VendorName;

    [field: AllowNull, MaybeNull]
    public override string GameRegistryKeyName => field ??= Path.GetFileNameWithoutExtension(ExecutableName);

    [field: AllowNull, MaybeNull]
    public override string ProfileName => field ??= "WuwaGlobal";

    [field: AllowNull, MaybeNull]
    public override string ZoneDescription => field ??=
        "Wuthering Waves is a story-rich open-world action RPG with a high degree of freedom. You wake from your slumber as Rover, " +
        "joined by a vibrant cast of Resonators on a journey to reclaim your lost memories and surmount the Lament.";

    [field: AllowNull, MaybeNull]
    public override string ZoneName => field ??= "Global";

    [field: AllowNull, MaybeNull]
    public override string ZoneFullName => field ??= GameName + " (" + ZoneName + " )";

    [field: AllowNull, MaybeNull]
    public override string ZoneLogoUrl => field ??= "https://cdn.collapselauncher.com/cl-cdn/inhouse-plugin/wuwa/wuthering-waves-logo.png";

    [field: AllowNull, MaybeNull]
    public override string ZonePosterUrl => field ??= "https://cdn.collapselauncher.com/cl-cdn/metadata/game_posters/poster_wuwa.png";

    [field: AllowNull, MaybeNull]
    public override string ZoneHomePageUrl => field ??= "https://wutheringwaves.kurogames.com/en/main";

    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.ClosedBeta;

    [field: AllowNull, MaybeNull]
    public override string GameMainLanguage => field ??= "en";

    [field: AllowNull, MaybeNull]
    public override string LauncherGameDirectoryName => field ??= "Wuthering Waves Game";
    
    [field: AllowNull, MaybeNull]
    public override List<string> SupportedLanguages => field ??= [
        "Japanese",
        "English",
        "Chinese",
        "Chinese (Traditional)",
        "Korean",
        "French",
        "German",
        "Spanish"
    ];

    public override ILauncherApiMedia? LauncherApiMedia 
    {
        get => field ??= new WuwaGlobalLauncherApiMedia(ApiResponseUrl, CurrentTag, AuthenticationHash, "bg", Hash1);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new WuwaGlobalLauncherApiNews(ApiResponseUrl, CurrentTag, AuthenticationHash, "news", Hash1);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new WuwaGameManager(ExecutableName, ApiResponseAssetUrl, AuthenticationHash, CurrentTag, ClientAccess, CurrentPatch, Hash1, Hash2);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new WuwaGameInstaller(GameManager);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);
}
