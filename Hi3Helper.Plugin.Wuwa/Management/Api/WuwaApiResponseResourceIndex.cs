using System.Text.Json.Serialization;
// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseResourceIndex
{
    [JsonPropertyName("resource")]
    public WuwaApiResponseResourceEntry[] Resource { get; set; } = [];
}

public class WuwaApiResponseResourceEntry
{
    [JsonPropertyName("dest")]
    public string? Dest { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("size")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong Size { get; set; }

    [JsonPropertyName("chunkInfos")]
    public WuwaApiResponseResourceChunkInfo[]? ChunkInfos { get; set; }
}

public class WuwaApiResponseResourceChunkInfo
{
    [JsonPropertyName("start")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong Start { get; set; }

    [JsonPropertyName("end")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong End { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
}