using Hi3Helper.Plugin.Core.Utility.Json.Converters;
using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseNews
{
    [JsonPropertyName("guidance")]
    public WuwaApiResponseNewsData? NewsData { get; set; }

    [JsonPropertyName("slideshow")]
    public WuwaApiResponseCarouselEntry[] CarouselData { get; set; } = [];
}

public class WuwaApiResponseNewsData
{
    [JsonPropertyName("activity")]
    public WuwaApiResponseNewsKind ContentKindEvent { get; set; } = new();

    [JsonPropertyName("notice")]
    public WuwaApiResponseNewsKind ContentKindNotice { get; set; } = new();

    [JsonPropertyName("news")]
    public WuwaApiResponseNewsKind ContentKindNews { get; set; } = new();
}

public class WuwaApiResponseNewsKind
{
    [JsonPropertyName("contents")]
    public WuwaApiResponseNewsEntry[] Contents { get; set; } = [];
}

public class WuwaApiResponseNewsEntry
{
    [JsonPropertyName("content")]
    public string? NewsTitle { get; set; }

    [JsonPropertyName("jumpUrl")]
    public string? ClickUrl { get; set; }

    [JsonPropertyName("time")]
    public string? Date { get; set; }
}

public class WuwaApiResponseCarouselEntry
{
    [JsonPropertyName("url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("jumpUrl")]
    public string? ClickUrl { get; set; }

    [JsonPropertyName("md5")]
    [JsonConverter(typeof(HexStringToArrayJsonConverter<byte>))]
    public byte[] ImageHashMd5 { get; set; } = [];

    public string? Description  { get; set; }
}