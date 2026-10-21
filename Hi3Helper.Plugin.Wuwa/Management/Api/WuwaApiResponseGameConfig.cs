using System.Text.Json.Serialization;
using System.Collections.Generic;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseGameConfigDefinition
{
    [JsonPropertyName("cdnList")]
    public WuwaApiResponseCdn[]? CdnList { get; set; }

    [JsonPropertyName("config")] // Mapping: root -> default -> config
    public WuwaApiResponseGameConfigRef? ConfigReference { get; set; }

    [JsonPropertyName("bundles")]
    public Dictionary<string, WuwaApiResponseBundle>? Bundles { get; set; }

    [JsonPropertyName("resourcePacks")]
    public Dictionary<string, WuwaApiResponseGameConfigRef>? ResourcePacks { get; set; }
}

public class WuwaApiResponseBundle
{
    [JsonPropertyName("resourcePacks")]
    public string[] ResourcePacks { get; set; } = [];
}

public class WuwaApiResponseCdn
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class WuwaApiResponseGameConfig
{
    [JsonPropertyName("cdnList")]
    public WuwaApiResponseCdn[]? CdnList { get; set; }

    [JsonPropertyName("bundles")]
    public Dictionary<string, WuwaApiResponseBundle>? Bundles { get; set; }

    [JsonPropertyName("resourcePacks")]
    public Dictionary<string, WuwaApiResponseGameConfigRef>? ResourcePacks { get; set; }

    [JsonPropertyName("default")] // Mapping: root -> default
    public WuwaApiResponseGameConfigDefinition? Default { get; set; }

    [JsonPropertyName("predownload")] // Mapping: root -> predownload
    public WuwaApiResponseGameConfigDefinition? PredownloadReference { get; set; }

    [JsonPropertyName("keyFileCheckList")] // Mapping: root -> keyFileCheckList[]
    public string[]? KeyFileCheckList { get; set; }
}
