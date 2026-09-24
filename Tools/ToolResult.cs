using System.Text.Json;

namespace Opc.Ua.Cloud.AI.Tools
{
    /// <summary>
    /// Shapes tool return values.
    ///
    /// <para>
    /// Everything a tool returns is injected into a language model's context window, so two
    /// things matter beyond correctness: the payload must be bounded, and a failure must read
    /// as an instruction rather than a stack trace. <see cref="JsonCapped"/> handles the first
    /// by truncating long arrays and saying so explicitly - a silently shortened list would
    /// lead the model to conclude the plant has fewer assets than it does.
    /// </para>
    /// </summary>
    internal static class ToolResult
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false
        };

        public static string Json(JsonElement element)
        {
            return (element.ValueKind == JsonValueKind.Undefined)
                ? "{}"
                : element.GetRawText();
        }

        /// <summary>
        /// Serializes a payload, truncating the largest array in it to <paramref name="maxItems"/>
        /// and appending an explicit note when it does.
        /// </summary>
        public static string JsonCapped(JsonElement element, int maxItems)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                return "{}";
            }

            // Top-level array: cap directly.
            if (element.ValueKind == JsonValueKind.Array)
            {
                int total = element.GetArrayLength();
                if (total <= maxItems)
                {
                    return element.GetRawText();
                }

                string items = string.Join(",", element.EnumerateArray().Take(maxItems).Select(e => e.GetRawText()));
                return $"{{\"truncated\":true,\"returned\":{maxItems},\"total\":{total}," +
                       $"\"note\":\"Only the first {maxItems} of {total} items are shown. Narrow the query to see the rest.\"," +
                       $"\"items\":[{items}]}}";
            }

            // Object wrapping a result array (the I3X {success, result:[...]} envelope).
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((property.Value.ValueKind == JsonValueKind.Array) &&
                        (property.Value.GetArrayLength() > maxItems))
                    {
                        int total = property.Value.GetArrayLength();
                        string items = string.Join(",", property.Value.EnumerateArray().Take(maxItems).Select(e => e.GetRawText()));
                        return $"{{\"truncated\":true,\"returned\":{maxItems},\"total\":{total}," +
                               $"\"note\":\"Only the first {maxItems} of {total} '{property.Name}' entries are shown. Narrow the query to see the rest.\"," +
                               $"\"{property.Name}\":[{items}]}}";
                    }
                }
            }

            return element.GetRawText();
        }

        /// <summary>
        /// A non-exceptional message telling the model what went wrong and what to do instead.
        /// </summary>
        public static string Guidance(string message)
        {
            return JsonSerializer.Serialize(new { success = false, guidance = message }, Options);
        }
    }
}
