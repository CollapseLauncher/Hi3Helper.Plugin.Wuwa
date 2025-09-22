using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseGameConfigDefinition
{
    [JsonPropertyName("config")] // Mapping: root -> default -> config
    public WuwaApiResponseGameConfigRef? ConfigReference { get; set; }
}

public class WuwaApiResponseGameConfig
{
    [JsonPropertyName("default")] // Mapping: root -> default
    public WuwaApiResponseGameConfigDefinition? Default { get; set; }

    [JsonPropertyName("keyFileCheckList")] // Mapping: root -> keyFileCheckList[]
    public string[]? KeyFileCheckList { get; set; }
}