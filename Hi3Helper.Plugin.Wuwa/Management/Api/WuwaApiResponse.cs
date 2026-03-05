using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

[JsonSerializable(typeof(WuwaApiResponseMedia))]
[JsonSerializable(typeof(WuwaApiResponseNews))]
[JsonSerializable(typeof(WuwaApiResponseSocial))]
[JsonSerializable(typeof(WuwaApiResponseGameConfig))]
[JsonSerializable(typeof(WuwaApiResponsePatchIndex))]
[JsonSerializable(typeof(WuwaLauncherDownloadConfig))]
[JsonSerializable(typeof(WuwaApiResponseResourceIndex))]
public partial class WuwaApiResponseContext : JsonSerializerContext;
