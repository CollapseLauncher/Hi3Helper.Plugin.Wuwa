using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseGameConfigDefinition
#if USELIGHTWEIGHTJSONPARSER
    : IJsonElementParsable<WuwaApiResponseGameConfigDefinition>,
      IJsonStreamParsable<WuwaApiResponseGameConfigDefinition>
#endif
{
    [JsonPropertyName("config")] // default.config
    public WuwaApiResponseGameConfigRef? ConfigReference { get; set; }
}

public class WuwaApiResponseGameConfig
#if USELIGHTWEIGHTJSONPARSER
    : IJsonElementParsable<WuwaApiResponseGameConfig>,
      IJsonStreamParsable<WuwaApiResponseGameConfig>
#endif
{
    [JsonPropertyName("default")] // default
    public WuwaApiResponseGameConfigDefinition? Default { get; set; }

#if !USELIGHTWEIGHTJSONPARSER
    [JsonPropertyName("keyFileCheckList")] // keyFileCheckList[2]
    public string[]? KeyFileCheckList { get; set; }
#endif

#if USELIGHTWEIGHTJSONPARSER
    public static WuwaApiResponseGameConfig ParseFrom(Stream stream, bool isDisposeStream = false,
        JsonDocumentOptions options = default)
        => ParseFromAsync(stream, isDisposeStream, options).Result;

    public static async Task<WuwaApiResponseGameConfig> ParseFromAsync(Stream stream, bool isDisposeStream = false,
        JsonDocumentOptions options = default,
        CancellationToken token = default)
    {
        JsonDocument doc = await JsonDocument.ParseAsync(stream, options, token).ConfigureAwait(false);
        if (isDisposeStream)
            await stream.DisposeAsync().ConfigureAwait(false);
        return ParseFrom(doc.RootElement);
    }

    public static WuwaApiResponseGameConfig ParseFrom(JsonElement element)
    {
        WuwaApiResponseGameConfig returnValue = new WuwaApiResponseGameConfig
        {
            CurrentVersion = element.GetString("version") is { } versionStr
                ? new GameVersion(versionStr)
                : new GameVersion(),
            PatchType = element.GetString("patchType"),
            PatchFileSize = element.GetUInt64OrNull("size"),
            BaseUrl = element.GetString("baseUrl"),
            KeyFileCheckList = element.GetStringArray("keyFileCheckList")
        };

        return returnValue;
    }
#endif
}
