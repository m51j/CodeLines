using System.Text;

namespace CodeLines.Core.AiUsage.Agents.Adapters;

/// <summary>One decoded protobuf message: field number → values (double for varints, bytes for length-delimited).</summary>
internal sealed class ProtoMessage : Dictionary<double, List<object>>;

/// <summary>
/// Schema-less protobuf reader (ccstats' <c>pb()</c>). Varints are summed with multiplication in doubles, exactly as the
/// JavaScript does, so values past 32 bits survive and huge ones lose precision the same way.
/// </summary>
internal static class Protobuf
{
    public static ProtoMessage Decode(ReadOnlyMemory<byte> buffer)
    {
        var m = new ProtoMessage();
        var buf = buffer.Span;
        var i = 0;
        double Varint(ReadOnlySpan<byte> b)
        {
            double r = 0, s = 1;
            int x;
            do
            {
                x = i < b.Length ? b[i] : 0; // past the end reads as undefined → 0
                i++;
                r += (x & 0x7f) * s;
                s *= 128;
            } while ((x & 0x80) != 0 && i < b.Length);
            return r;
        }
        while (i < buf.Length)
        {
            var key = Varint(buf);
            var f = Math.Floor(key / 8);
            var t = key % 8;
            object? v = null;
            if (t == 0) v = Varint(buf);
            else if (t == 2)
            {
                var n = Varint(buf);
                var start = Math.Min(i, buf.Length);
                var end = n >= buf.Length - start ? buf.Length : start + (int)n;
                v = buffer[start..end];
                i = n >= buf.Length - start ? buf.Length : start + (int)n;
            }
            else if (t == 1) i += 8;
            else if (t == 5) i += 4;
            else return m; // not a message; keep what was read
            if (v is not null)
            {
                if (!m.TryGetValue(f, out var list)) m[f] = list = [];
                list.Add(v);
            }
        }
        return m;
    }

    /// <summary>The first value of field <paramref name="f"/> decoded as a message, or null when it is not bytes.</summary>
    public static ProtoMessage? Sub(ProtoMessage? m, double f) =>
        m?.GetValueOrDefault(f) is [ReadOnlyMemory<byte> bytes, ..] ? Decode(bytes) : null;

    /// <summary>The first value of field <paramref name="f"/> when it is a varint, else 0.</summary>
    public static double Int(ProtoMessage? m, double f) => m?.GetValueOrDefault(f) is [double d, ..] ? d : 0;

    /// <summary>The first value of field <paramref name="f"/> as UTF-8 text when it is bytes, else "".</summary>
    public static string Str(ProtoMessage? m, double f) =>
        m?.GetValueOrDefault(f) is [ReadOnlyMemory<byte> bytes, ..] ? Encoding.UTF8.GetString(bytes.Span) : "";
}
