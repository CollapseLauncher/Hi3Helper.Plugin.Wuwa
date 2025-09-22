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
public partial class WuwaGlobalPresetConfig : PluginPresetConfigBase
{
    private const string ApiResponseUrl = "https://prod-alicdn-gamestarter.kurogame.com/";
    // private const string CurrentUninstKey = "";
    private const string CurrentTag = "G153";
    private const string CurrentPatchHash = "VlNTU1c8DAEsKzslESUCDRIQAiomLA4WKBEMIAABOQgyMSEgVAA";
    private const string Hash1 = "VxvH4SpEIzJaYhAODsY2Yjw1TyCrdm0t";
    private const string ExecutableName = "WutheringWaves.exe";
    private const string VendorName = "Kuro Games";

    [field: AllowNull, MaybeNull]
    public override string GameName => field ??= "Wuthering Waves";

    public override string GameExecutableName => ExecutableName;

    public override string GameAppDataPath
    {
        get
        {
            string? gamePath = null;
            GameManager?.GetGamePath(out gamePath);
            return string.IsNullOrEmpty(gamePath) ? string.Empty : Path.Combine(gamePath, "Client");
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

    // TODO for Cry0:
    // Please define what languages are supported on WuWa
    [field: AllowNull, MaybeNull]
    public override List<string> SupportedLanguages => field ??= [
        "Japanese",
        "English"
    ];

    public override ILauncherApiMedia? LauncherApiMedia 
    {
        get => field ??= new WuwaGlobalLauncherApiMedia(ApiResponseUrl, CurrentTag, CurrentPatchHash, "bg", Hash1);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => field ??= new WuwaGlobalLauncherApiNews(ApiResponseUrl, CurrentTag, CurrentPatchHash, "news", Hash1);
        set;
    }

    public override IGameManager? GameManager
    {
        get => field ??= new WuwaGameManager(ExecutableName, ApiResponseUrl, CurrentPatchHash, CurrentTag, Hash1);
        set;
    }

    public override IGameInstaller? GameInstaller
    {
        get => field ??= new WuwaGameInstaller(GameManager);
        set;
    }

    protected override Task<int> InitAsync(CancellationToken token) => Task.FromResult(0);
}
