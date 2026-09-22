using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Offload.Core.Util;

public static class Json
{
    /// <summary>Настройки для собственных файлов Offload (camelCase, отступы, кириллица без экранирования).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Компактный вариант (одна строка) — для JSONL и IPC.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Разбор чужих JSONC-файлов (комментарии и висячие запятые допускаются).</summary>
    public static readonly JsonDocumentOptions LenientDocument = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly System.Text.Json.Nodes.JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
