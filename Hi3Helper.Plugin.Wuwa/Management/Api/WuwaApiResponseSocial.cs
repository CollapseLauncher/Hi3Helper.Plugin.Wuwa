using System.Collections.Generic;
using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

// ReSharper disable InconsistentNaming

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseSocial
{
    [JsonPropertyName("data")] // Mapping: root -> data[]
    public List<WuwaApiResponseSocialResponse>? SocialMediaEntries { get; set; }
}

public class WuwaApiResponseSocialResponse
{
    [JsonPropertyName("iconJumpUrl")] // Mapping: root -> data[] -> iconJumpUrl
    public string? ClickUrl { get; set; }
    
    [JsonPropertyName("icon")] // Mapping: root -> data[] -> icon
    public string? IconUrl { get; set; }
    
    [JsonPropertyName("name")] // Mapping: root -> data[] -> name
    public string? SocialMediaName { get; set; }
}
