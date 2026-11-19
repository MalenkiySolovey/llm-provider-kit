using System.Globalization;
using System.Text.Json;
using Llm.ProviderKit.Catalog;

namespace Llm.ProviderKit.Inspection;

/// <summary>RFC 6901 pointer navigation and bounded scalar/list extraction.</summary>
public static class JsonPointerExtractor
{
    public static bool TryResolvePointer(JsonElement root, string jsonPath, out JsonElement target)
    {
        target = default;
        if (jsonPath.Length == 0)
        {
            target = root;
            return true;
        }

        if (!jsonPath.StartsWith('/')) return false;
        var current = root;
        foreach (var segment in jsonPath[1..].Split('/'))
        {
            var token = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out var value))
                {
                    var found = false;
                    foreach (var property in current.EnumerateObject())
                    {
                        if (!property.Name.Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
                        value = property.Value;
                        found = true;
                        break;
                    }

                    if (!found) return false;
                }

                current = value;
            }
            else if (current.ValueKind == JsonValueKind.Array &&
                     int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                     index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return false;
            }
        }

        target = current;
        return true;
    }

    public static bool TryExtractDecimal(JsonElement root, JsonFieldMapping mapping, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(mapping.JsonPath) || !TryResolvePointer(root, mapping.JsonPath, out var element)) return false;
        var valid = element.ValueKind == JsonValueKind.Number
            ? element.TryGetDecimal(out value)
            : element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        if (!valid) return false;

        try
        {
            if (mapping.Transform?.Equals("scale", StringComparison.OrdinalIgnoreCase) == true) value = checked(value * (mapping.ScaleFactor ?? 1m));
            else if (mapping.Transform?.Equals("centsToUnits", StringComparison.OrdinalIgnoreCase) == true) value /= 100m;
            return true;
        }
        catch (OverflowException)
        {
            value = 0m;
            return false;
        }
    }

    public static bool TryExtractString(JsonElement root, string jsonPath, out string? value)
    {
        value = null;
        if (!TryResolvePointer(root, jsonPath, out var element)) return false;
        if (element.ValueKind == JsonValueKind.String) value = element.GetString();
        else if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) value = element.GetRawText();
        return value is not null;
    }

    public static bool TryExtractStringArray(JsonElement root, string jsonPath, out List<string> items)
    {
        items = [];
        if (!TryResolvePointer(root, jsonPath, out var element) || element.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in element.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(id)) items.Add(id);
        }

        return items.Count > 0;
    }
}
