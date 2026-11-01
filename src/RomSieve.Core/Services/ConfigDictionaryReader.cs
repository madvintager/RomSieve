using System.Globalization;
using System.Text.Json;

namespace RomSieve.Core.Services;

/// <summary>
/// Typed, tolerant readers for the loosely-typed configuration dictionary produced by
/// <c>IConfigurationService</c> (values may be CLR primitives, collections or <see cref="JsonElement"/>).
/// Every method returns <c>false</c> when the key is missing or null instead of throwing.
/// </summary>
public static class ConfigDictionaryReader
{
    public static bool TryGetString(IReadOnlyDictionary<string, object?> data, string key, out string value)
    {
        value = string.Empty;
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is string str)
        {
            value = str;
            return true;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.String)
        {
            value = je.GetString() ?? string.Empty;
            return true;
        }

        value = raw.ToString() ?? string.Empty;
        return true;
    }

    public static bool TryGetBool(IReadOnlyDictionary<string, object?> data, string key, out bool value)
    {
        value = false;
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                value = false;
                return true;
            default:
                return bool.TryParse(raw.ToString(), out value);
        }
    }

    public static bool TryGetInt(IReadOnlyDictionary<string, object?> data, string key, out int value)
    {
        value = 0;
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            default:
                return int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    public static bool TryGetDouble(IReadOnlyDictionary<string, object?> data, string key, out double value)
    {
        value = 0;
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var parsed):
                value = parsed;
                return true;
            default:
                return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    public static bool TryGetStringList(IReadOnlyDictionary<string, object?> data, string key, out List<string> values)
    {
        values = [];
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is IEnumerable<string> strEnum)
        {
            values = strEnum.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return true;
        }

        if (raw is IEnumerable<object?> objEnum)
        {
            values = objEnum
                .Select(o => o?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } je)
        {
            foreach (var elem in je.EnumerateArray())
            {
                var item = elem.ValueKind == JsonValueKind.String ? elem.GetString() : elem.ToString();
                if (!string.IsNullOrWhiteSpace(item))
                {
                    values.Add(item);
                }
            }

            return true;
        }

        return false;
    }

    public static bool TryGetIntList(IReadOnlyDictionary<string, object?> data, string key, out List<int> values)
    {
        values = [];
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is IEnumerable<int> intEnum)
        {
            values = intEnum.ToList();
            return true;
        }

        if (raw is IEnumerable<object?> objEnum)
        {
            foreach (var o in objEnum)
            {
                if (int.TryParse(o?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    values.Add(parsed);
                }
            }

            return values.Count > 0;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } je)
        {
            foreach (var elem in je.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var parsed))
                {
                    values.Add(parsed);
                }
            }

            return values.Count > 0;
        }

        return false;
    }
}
