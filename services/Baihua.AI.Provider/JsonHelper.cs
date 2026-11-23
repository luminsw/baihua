using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Baihua.AI.Provider
{
    /// <summary>
    /// JSON 辅助方法
    /// </summary>
    public static class JsonHelper
    {
        public static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true };
        public static JsonSerializerOptions Compact { get; } = new() { WriteIndented = false };
        public static JsonSerializerOptions IndentedUnicode { get; } = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        public static JsonSerializerOptions CaseInsensitive { get; } = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static string GetString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? "";
            return "";
        }

        public static long GetLong(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt64(out var val)) return val;
                if (prop.TryGetInt32(out var intVal)) return intVal;
            }
            return 0;
        }
    }
}
