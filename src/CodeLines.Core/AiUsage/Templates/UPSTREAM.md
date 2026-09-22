# ccstats templates

These pages are copied verbatim from ccstats (`D:\min\2026\ccstats`), extracted on 2026-09-23:

| File | Source |
|---|---|
| `claude-report.html` | `renderHtml()` in `src/render.mjs` |
| `agent-report.html` | `renderAgentHtml()` in `src/agents/render.mjs` |
| `dashboard.html` | `renderDashboard()` in `src/agents/dashboard.mjs` |

The only edits are placeholders for what ccstats interpolates on the server:

- `/*__CCSTATS_PAYLOAD__*/`: the JSON payload.
- `__CCSTATS_TITLE__`: the HTML-escaped page title.
- `/*__CCSTATS_TABS__*/`: the dashboard's tab buttons.
- `/*__CCSTATS_GENERATED__*/`: the dashboard's "generated" time.

To re-sync after a ccstats update:

1. Call each render function with a marker payload.
2. Replace the markers with the placeholders above.
3. Keep the payload shape in `AiUsagePayloadWriter.cs` in step with the upstream `aggregate.mjs` files.
