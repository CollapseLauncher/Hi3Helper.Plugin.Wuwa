using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.Wuwa.Management.Settings;
using System;
using System.Collections.Generic;

namespace Hi3Helper.Plugin.Wuwa;

public partial class Exports
{
    private static readonly Dictionary<string, WuwaGameSettings> PendingGameSettings = new(StringComparer.Ordinal);
    private static readonly object GameSettingsLock = new();

    protected override GameSettingsPage? GetGameSettingsPageCore(IPluginPresetConfig presetConfig)
    {
        presetConfig.comGet_ProfileName(out string profileName);
        lock (GameSettingsLock)
        {
            WuwaGameSettings settings = WuwaGameSettings.Load(presetConfig);
            PendingGameSettings[profileName] = settings;
            return settings.CreatePage();
        }
    }

    protected override void SetGameSettingValueCore(IPluginPresetConfig presetConfig, string key, string value)
    {
        presetConfig.comGet_ProfileName(out string profileName);
        lock (GameSettingsLock)
        {
            GetPending(profileName).SetValue(key, value);
        }
    }

    protected override void ApplyGameSettingsCore(IPluginPresetConfig presetConfig)
    {
        presetConfig.comGet_ProfileName(out string profileName);
        lock (GameSettingsLock)
        {
            WuwaGameSettings settings = GetPending(profileName);
            settings.Apply();
            PendingGameSettings[profileName] = WuwaGameSettings.Load(presetConfig);
        }
    }

    private static WuwaGameSettings GetPending(string profileName) =>
        PendingGameSettings.TryGetValue(profileName, out WuwaGameSettings? settings)
            ? settings
            : throw new InvalidOperationException("Open the Wuthering Waves game settings page before changing settings.");
}
