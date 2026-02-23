using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Utils;
using System.Text.Json.Serialization;
// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseGameConfigRef
{
    [JsonPropertyName("indexFile")] // Mapping: root -> default -> config -> indexFile
    public string? IndexFile { get; set; }

    [JsonPropertyName("version")] // Mapping: root -> default -> config -> version
    [JsonConverter(typeof(GameVersionJsonConverter))]
    public GameVersion CurrentVersion { get; set; }

    [JsonPropertyName("patchType")] // Mapping: root -> default -> config -> patchType
    public string? PatchType { get; set; }

    [JsonPropertyName("size")] // Mapping: root -> default -> config -> size
    // NOTE: For old-style patchConfig entries this equals the full game content size (same
    // as unCompressSize), NOT the actual patch download size. Always prefer computing the
    // real download size from the patch index krpdiff entries instead of using this field.
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? PatchFileSize { get; set; }

    [JsonPropertyName("baseUrl")] // Mapping: root -> default -> config -> baseUrl
    public string? BaseUrl { get; set; }

    [JsonPropertyName("patchConfig")] // Mapping: root -> default -> config -> patchConfig
	public WuwaApiResponseGameConfigRef[]? PatchConfig
    { get; set; }
}