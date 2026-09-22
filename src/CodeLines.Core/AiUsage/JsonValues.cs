using System.Globalization;
using System.Text.Json;

namespace CodeLines.Core.AiUsage;

/// <summary>JavaScript-style lenient reads used by every transcript parser.</summary>
internal static class JsonValues
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Prop(this JsonElement? element, string name) => element is { } e ? e.Prop(name) : null;

    public static bool IsObject(this JsonElement? element) => element is { ValueKind: JsonValueKind.Object };

    /// <summary>ccstats' <c>num()</c>: finite numbers only, anything else is 0.</summary>
    public static double Num(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    public static long Long(this JsonElement? element) => (long)element.Num();

    public static bool IsNumber(this JsonElement? element) => element is { ValueKind: JsonValueKind.Number };

    /// <summary><c>value || ''</c> for string values; non-strings are treated as missing.</summary>
    public static string Str(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } e ? e.GetString() ?? "" : "";

    /// <summary>JavaScript truthiness, for <c>d.isSidechain ? 1 : 0</c>-style checks.</summary>
    public static bool Truthy(this JsonElement? element) => element switch
    {
        null => false,
        { ValueKind: JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array } => true,
        { ValueKind: JsonValueKind.String } e => e.GetString()?.Length > 0,
        { ValueKind: JsonValueKind.Number } e => e.GetDouble() != 0,
        _ => false
    };

    /// <summary><c>Date.parse</c> for the ISO-8601 timestamps agents write; null when unparseable.</summary>
    public static long? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var styles = text.Length <= 10 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var value)
            ? value.ToUnixTimeMilliseconds() : null;
    }
}
