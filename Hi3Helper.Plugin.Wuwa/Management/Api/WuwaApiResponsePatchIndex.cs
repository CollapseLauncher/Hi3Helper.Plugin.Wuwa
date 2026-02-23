using System.Text.Json.Serialization;
// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

/// <summary>
/// Represents the patch index JSON returned by a patchConfig's indexFile URL.
/// Contains the resource files (krpdiff patches), files to delete, and
/// groupInfos mapping source files to destination files for patching.
/// </summary>
public class WuwaApiResponsePatchIndex
{
    [JsonPropertyName("resource")]
    public WuwaApiResponseResourceEntry[] Resource { get; set; } = [];

    [JsonPropertyName("deleteFiles")]
    public WuwaApiResponsePatchDeleteEntry[] DeleteFiles { get; set; } = [];

    [JsonPropertyName("groupInfos")]
    public WuwaApiResponsePatchGroupInfo[] GroupInfos { get; set; } = [];
}

/// <summary>
/// Represents a file to be deleted during patch application.
/// </summary>
public class WuwaApiResponsePatchDeleteEntry
{
    [JsonPropertyName("dest")]
    public string? Dest { get; set; }
}

/// <summary>
/// Maps source files to destination files for krpdiff patch application.
/// Each group represents a single patch operation: apply the krpdiff to
/// srcFiles[].dest to produce dstFiles[].dest.
/// </summary>
public class WuwaApiResponsePatchGroupInfo
{
    [JsonPropertyName("srcFiles")]
    public WuwaApiResponsePatchFileRef[] SrcFiles { get; set; } = [];

    [JsonPropertyName("dstFiles")]
    public WuwaApiResponsePatchFileRef[] DstFiles { get; set; } = [];
}

/// <summary>
/// Represents a file reference within a groupInfo entry (either source or destination).
/// </summary>
public class WuwaApiResponsePatchFileRef
{
    [JsonPropertyName("dest")]
    public string? Dest { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("size")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong Size { get; set; }
}
