using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseMedia
{
    [JsonPropertyName("backgroundFile")] // Mapping: root -> backgroundFile
    public string? BackgroundImageUrl { get; set; }
}

