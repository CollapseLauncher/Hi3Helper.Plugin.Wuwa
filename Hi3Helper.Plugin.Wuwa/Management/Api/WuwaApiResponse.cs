using System.Net;
using System.Net.Http;

#if !USELIGHTWEIGHTJSONPARSER
using System.Text.Json.Serialization;
#else
using Hi3Helper.Plugin.Core.Utility.Json;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

#if !USELIGHTWEIGHTJSONPARSER
[JsonSerializable(typeof(WuwaApiResponseMedia))]
[JsonSerializable(typeof(WuwaApiResponseSocial))]
[JsonSerializable(typeof(WuwaApiResponseGameConfig))]
// [JsonSerializable(typeof(WuwaApiResponse<WuwaApiResponseGameConfig>))]
// [JsonSerializable(typeof(WuwaApiResponse<WuwaApiResponseGameConfigRef>))]
public partial class WuwaApiResponseContext : JsonSerializerContext;
#endif

