using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.HBR.Management.PresetConfig;
using Hi3Helper.Plugin.HBR.Utility;
using System;
using System.Runtime.InteropServices.Marshalling;

// ReSharper disable InconsistentNaming
namespace Hi3Helper.Plugin.Wuwa;

[GeneratedComClass]
public partial class HBRPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances = [new HBRGlobalPresetConfig()];
    private static DateTime _pluginCreationDate = new(2025, 07, 20, 05, 06, 0, DateTimeKind.Utc);
    private static IPluginSelfUpdate? _selfUpdaterInstance;

    public override void GetPluginName(out string result) => result = "Wuthering Waves Plugin";

    public override void GetPluginDescription(out string result) => result = "A plugin for Wuthering Waves in Collapse Launcher";

    public override void GetPluginAuthor(out string result) => result = "CryoTechnic, Collapse Project Team";

    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _pluginCreationDate.AsPointer();

    public override void GetPresetConfigCount(out int count) => count = PresetConfigInstances.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        // Avoid crash by returning null if index is out of bounds
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            presetConfig = null!;
            return;
        }

        // Return preset config at index (n)
        presetConfig = PresetConfigInstances[index];
    }

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate selfUpdate) => selfUpdate = _selfUpdaterInstance ??= new HBRPluginSelfUpdate();

    private string? _getPluginAppIconUrl;
    public override void GetPluginAppIconUrl(out string result) => result = _getPluginAppIconUrl ??= Convert.ToBase64String(HBRIconData.HBRAppIconData);

    private string? _getNotificationPosterUrl;
    public override void GetNotificationPosterUrl(out string result) => result = _getNotificationPosterUrl ??= Convert.ToBase64String(HBRIconData.HBRAppPosterData);
}
