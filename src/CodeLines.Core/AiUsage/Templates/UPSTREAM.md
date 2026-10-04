# ccstats templates

These pages originate from ccstats (`D:\min\2026\ccstats`), extracted on 2026-09-23:

| File | Source |
|---|---|
| `claude-report.html` | `renderHtml()` in `src/render.mjs` |
| `agent-report.html` | `renderAgentHtml()` in `src/agents/render.mjs` |
| `dashboard.html` | `renderDashboard()` in `src/agents/dashboard.mjs` |

Placeholders replace what ccstats interpolates on the server:

- `/*__CCSTATS_PAYLOAD__*/`: the JSON payload.
- `__CCSTATS_TITLE__`: the HTML-escaped page title.
- `/*__CCSTATS_TABS__*/`: the dashboard's tab buttons.
- `/*__CCSTATS_GENERATED__*/`: the dashboard's "generated" time.

## CodeLines display customizations

Both report templates customize the Projects tab without changing payloads or aggregation:

- `project-scroll` wraps the project and heaviest-session tables with keyboard-focusable, named scroll regions. Height is capped at `min(60vh, 640px)` so short tables remain compact.
- Table headers stick to the top and `project-column` cells stick to the left, with opaque backgrounds and separate borders to avoid overlap and disappearing border lines.
- `projectCell()` displays the full project path as selectable text in a `project-path` span. Its width adapts between 160px and 320px, overflow is ellipsized, and the full path is also its native tooltip. DOM serialization escapes both text and the tooltip safely.

To re-sync after a ccstats update:

1. Call each render function with a marker payload.
2. Replace the markers with the placeholders above.
3. Keep the payload shape in `AiUsagePayloadWriter.cs` in step with the upstream `aggregate.mjs` files.
4. Reapply the Projects tab customizations above to both report templates, including the project column in the heaviest-session table. Verify scrolling in standalone, dashboard iframe, and single-file exported reports.
