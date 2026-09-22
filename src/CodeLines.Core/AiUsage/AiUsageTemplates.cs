using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodeLines.Core.AiUsage;

/// <summary>
/// Fills ccstats' pages, embedded verbatim from render.mjs, agents/render.mjs and agents/dashboard.mjs
/// (see Templates/UPSTREAM.md). Only the values ccstats interpolates on the server are replaced.
/// </summary>
public static class AiUsageTemplates
{
    private const string PayloadMarker = "/*__CCSTATS_PAYLOAD__*/";
    private const string TitleMarker = "__CCSTATS_TITLE__";
    private const string TabsMarker = "/*__CCSTATS_TABS__*/";
    private const string GeneratedMarker = "/*__CCSTATS_GENERATED__*/";

    private static readonly Lazy<string> ClaudeTemplate = new(() => Load("claude-report.html"));
    private static readonly Lazy<string> AgentTemplate = new(() => Load("agent-report.html"));
    private static readonly Lazy<string> DashboardTemplate = new(() => Load("dashboard.html"));

    public static string RenderClaude(string payloadJson) => ClaudeTemplate.Value.Replace(PayloadMarker, payloadJson, StringComparison.Ordinal);

    public static string RenderAgent(string title, string payloadJson) => AgentTemplate.Value
        .Replace(TitleMarker, Escape(title), StringComparison.Ordinal)
        .Replace(PayloadMarker, payloadJson, StringComparison.Ordinal);

    /// <summary>The shell page: one tab per page, each loaded into an iframe on first view.</summary>
    public static string RenderDashboard(IReadOnlyList<AiUsageTab> tabs, DateTimeOffset generatedAt, TimeZoneInfo? zone = null)
    {
        var buttons = string.Join("\n      ", tabs.Select((t, i) =>
            $"<button role=\"tab\" data-id=\"{Escape(t.Id)}\" data-href=\"{Escape(t.Href)}\" aria-selected=\"{(i == 0 ? "true" : "false")}\">"
            + $"{Escape(t.Label)} <span class=\"n\">{FormatTokens(t.Tokens)}</span></button>"));
        var local = TimeZoneInfo.ConvertTime(generatedAt, zone ?? TimeZoneInfo.Local);
        var generated = local.ToString("M/d/yyyy, h:mm:ss tt", CultureInfo.GetCultureInfo("en-US"));
        return DashboardTemplate.Value
            .Replace(TabsMarker, buttons, StringComparison.Ordinal)
            .Replace(GeneratedMarker, Escape(generated), StringComparison.Ordinal);
    }

    /// <summary>
    /// One self-contained file: the dashboard with every page embedded as JSON and loaded through iframe srcdoc,
    /// so the report can be shared without its folder.
    /// </summary>
    public static string InlineDashboard(string dashboardHtml, IReadOnlyDictionary<string, string> pagesByHref)
    {
        const string loader = "f.src = b.dataset.href;";
        if (!dashboardHtml.Contains(loader, StringComparison.Ordinal))
            throw new InvalidOperationException("The dashboard template no longer loads pages the expected way.");
        var encoder = new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default };
        var embedded = new StringBuilder();
        foreach (var (href, html) in pagesByHref)
            embedded.Append("<script type=\"application/json\" data-page=\"").Append(Escape(href)).Append("\">")
                .Append(JsonSerializer.Serialize(html, encoder)).Append("</script>\n");
        return dashboardHtml
            .Replace(loader, "f.srcdoc = JSON.parse(document.querySelector('script[data-page=\"'+CSS.escape(b.dataset.href)+'\"]').textContent);", StringComparison.Ordinal)
            .Replace("<main id=\"main\"></main>", "<main id=\"main\"></main>\n" + embedded, StringComparison.Ordinal);
    }

    /// <summary>ccstats' fmtTok for tab badges.</summary>
    public static string FormatTokens(double n) => n switch
    {
        >= 1e9 => (n / 1e9).ToString("F2", CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (n / 1e6).ToString("F1", CultureInfo.InvariantCulture) + "M",
        >= 1e3 => (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "K",
        _ => Math.Round(n, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>ccstats' esc(): &amp; &lt; &gt; " as numeric character references.</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c is '&' or '<' or '>' or '"' ? $"&#{(int)c};" : c.ToString());
        return sb.ToString();
    }

    private static string Load(string name)
    {
        using var stream = typeof(AiUsageTemplates).Assembly.GetManifestResourceStream("CodeLines.Core.AiUsage.Templates." + name)
            ?? throw new InvalidOperationException($"Missing embedded template {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
