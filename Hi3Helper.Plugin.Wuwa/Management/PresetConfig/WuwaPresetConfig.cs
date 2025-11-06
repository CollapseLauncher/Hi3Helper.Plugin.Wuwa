using Hi3Helper.Plugin.Core.Management.PresetConfig;
using System.Runtime.InteropServices.Marshalling;

namespace Hi3Helper.Plugin.Wuwa.Management.PresetConfig;

[GeneratedComClass]
public abstract partial class WuwaPresetConfig : PluginPresetConfigBase
{
	public abstract string? StartExecutableName { get; }
}
