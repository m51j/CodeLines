using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>JavaScript coercions the ccstats adapters rely on (String(), Number(), Math.round, decodeURIComponent, ||).</summary>
internal static class JsCompat
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>JavaScript's Number::toString: shortest round-trip digits, no ".0", exponent only outside [1e-6, 1e21).</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsInfinity(value)) return value > 0 ? "Infinity" : "-Infinity";
        if (value == 0) return "0";
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOf('E');
        var exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? r : r[..e];
        var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "");
        // value = 0.<digits> * 10^n
        var n = (dot < 0 ? mantissa.Length : dot) + exponent;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        n -= lead;
        var k = digits.Length;
        string s;
        if (k <= n && n <= 21) s = digits + new string('0', n - k);
        else if (0 < n && n <= 21) s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
        else
        {
            var exp = n - 1;
            var sign = exp < 0 ? "-" : "+";
            s = (k == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + sign + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
        }
        return value < 0 ? "-" + s : s;
    }

    /// <summary><c>String(value)</c> for a JSON value; a missing value is <c>undefined</c>.</summary>
    public static string Text(JsonElement? value) => value switch
    {
        null => "undefined",
        { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        { ValueKind: JsonValueKind.Number } e => Number(e.GetDouble()),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Null } => "null",
        { ValueKind: JsonValueKind.Array } e => string.Join(",", e.EnumerateArray().Select(x =>
            x.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : Text(x))),
        { ValueKind: JsonValueKind.Object } => "[object Object]",
        _ => "undefined"
    };

    /// <summary><c>String(value)</c> for a node:sqlite column value.</summary>
    public static string Text(object? value) => value switch
    {
        null => "null",
        string s => s,
        double d => Number(d),
        byte[] b => string.Join(",", b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary><c>a || b || ... || ''</c> as a string: the first truthy value, else "".</summary>
    public static string TextOr(params JsonElement?[] values)
    {
        foreach (var v in values)
            if (v.Truthy()) return Text(v);
        return "";
    }

    /// <summary>JavaScript <c>Math.round</c>: halves round towards +∞.</summary>
    public static double Round(double value)
    {
        if (!double.IsFinite(value)) return value;
        var floor = Math.Floor(value);
        return value - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary>JavaScript <c>Number(string)</c>: trimmed, "" is 0, hex/octal/binary prefixes, NaN when invalid.</summary>
    public static double ToNumber(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return 0;
        if (s.Length > 2 && s[0] == '0' && char.ToLowerInvariant(s[1]) is var p && p is 'x' or 'o' or 'b')
        {
            var radix = p == 'x' ? 16 : p == 'o' ? 8 : 2;
            double r = 0;
            foreach (var c in s[2..])
            {
                var digit = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) - 'a' + 10 : 99;
                if (digit >= radix) return double.NaN;
                r = r * radix + digit;
            }
            return r;
        }
        if (s is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (s == "-Infinity") return double.NegativeInfinity;
        // .NET would also accept "NaN" and "∞"; JavaScript accepts only digits, sign, point and exponent.
        foreach (var c in s)
            if (!(char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-')) return double.NaN;
        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>JavaScript <c>decodeURIComponent</c>; null where it would throw (bad escape or invalid UTF-8).</summary>
    public static string? DecodeUriComponent(string text)
    {
        if (!text.Contains('%')) return text;
        var sb = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] != '%') { sb.Append(text[i++]); continue; }
            bytes.Clear();
            while (i < text.Length && text[i] == '%')
            {
                if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2])) return null;
                bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 3;
            }
            try { sb.Append(StrictUtf8.GetString(bytes.ToArray())); }
            catch (DecoderFallbackException) { return null; }
        }
        return sb.ToString();
    }

    /// <summary><c>container[key]</c> for an array or object, with JavaScript's key coercion; null when undefined.</summary>
    public static JsonElement? Index(JsonElement? container, JsonElement? key)
    {
        if (container is not { } c) return null;
        if (c.ValueKind == JsonValueKind.Object) return c.Prop(Text(key));
        if (c.ValueKind != JsonValueKind.Array) return null;
        var index = key switch
        {
            { ValueKind: JsonValueKind.Number } k => k.GetDouble(),
            { ValueKind: JsonValueKind.String } k when k.GetString() is { } s && Number(ToNumber(s)) == s => ToNumber(s),
            _ => -1
        };
        return index >= 0 && index < c.GetArrayLength() && index == Math.Floor(index) ? c[(int)index] : null;
    }

    /// <summary>A column's bytes, like <c>Buffer.from(value)</c> (strings are UTF-8 encoded; numbers throw).</summary>
    public static byte[] Bytes(object? value) => value switch
    {
        byte[] b => b,
        string s => Encoding.UTF8.GetBytes(s),
        _ => throw new InvalidDataException("Expected a blob or text column.")
    };
}
