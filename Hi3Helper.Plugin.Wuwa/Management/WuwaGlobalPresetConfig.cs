using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.Wuwa.Management
{
    internal class WuwaGlobalPresetConfig : PluginPresetConfigBase
    {
        private const string ApiResponseUrl = "https://api-launcher-en.yo-star.com/";
        private const string CurrentUninstKey = "";
        private const string CurrentTag = "";
        private const string ExecutableName = "WutheringWaves.exe";
        private const string VendorName = "Kuro Games";

        public override string? GameName => "Wuthering Waves";

        public override string GameExecutableName => ExecutableName;

        public override string GameAppDataPath {

            get {
                string? gamePath = null;
                GameManager?.GetGamePath(out gamePath);
                if (string.IsNullOrEmpty(gamePath))
                    return string.Empty;
                return Path.Combine(gamePath, "Client");
            }
        }

        public override string GameLogFileName => field ??= Path.Combine("Saved", "Logs", "Client.log");

        public override string GameVendorName => field ??= VendorName;

        public override string GameRegistryKeyName => field ??= Path.GetFileNameWithoutExtension(ExecutableName);

        public override string ProfileName => field ??= "WuwaGlobal";

        public override string ZoneDescription => "Wuthering Waves is a story-rich open-world action RPG with a high degree of freedom. You wake from your slumber as Rover, " +
            "joined by a vibrant cast of Resonators on a journey to reclaim your lost memories and surmount the Lament.";

        public override string ZoneName => field ??= "Global";

        public override string ZoneFullName => field ??= GameName + " (" + ZoneName + " )";

        public override string ZoneLogoUrl => "https://cdn.collapselauncher.com/cl-cdn/inhouse-plugin/wuwa/wuthering-waves-logo.png";

        public override string ZonePosterUrl => "https://cdn.collapselauncher.com/cl-cdn/metadata/game_posters/poster_wuwa.png";

        public override string ZoneHomePageUrl => field ??= "https://wutheringwaves.kurogames.com/en/main";

        public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.ClosedBeta;

        public override string GameMainLanguage => field ??= "en";

        public override string LauncherGameDirectoryName => field ??= "Wuthering Waves Game";

        public override List<string> SupportedLanguages => [];

        public override ILauncherApiMedia? LauncherApiMedia { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override ILauncherApiNews? LauncherApiNews { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override IGameManager? GameManager { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override IGameInstaller? GameInstaller { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    }
}
