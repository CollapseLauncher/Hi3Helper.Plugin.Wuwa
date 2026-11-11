using Hi3Helper.Plugin.Core.Management;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.Marshalling;

// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming

namespace Hi3Helper.Plugin.Wuwa.Management.PresetConfig;

[GeneratedComClass]
public partial class WuwaSteamPresetConfig : WuwaGlobalPresetConfig
{

	[field: AllowNull, MaybeNull]
	public override string ProfileName => field ??= "WuwaSteam";

	[field: AllowNull, MaybeNull]
	public override string ZoneName => field ??= "Steam";

	[field: AllowNull, MaybeNull]
	public override string ZoneFullName => field ??= "Wuthering Waves (Steam)";

	public override IGameManager? GameManager
	{
		get => field ??= new WuwaGameManager(ExecutableName, ApiResponseAssetUrl, AuthenticationHash, CurrentTag, Hash1) { GameConfigUrl = GlobalGameConfigUrl };
		set;
	}

	public override IGameInstaller? GameInstaller
	{
		get => field ??= new WuwaGameInstaller(GameManager);
		set;
	}
}
