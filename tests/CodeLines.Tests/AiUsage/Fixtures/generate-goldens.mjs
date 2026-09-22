// Regenerates the Claude Code parity fixture and the payloads ccstats itself produces for it.
//
//   node generate-goldens.mjs <path to ccstats>
//
// The C# port must produce the same payloads (ClaudeParityTests). Everything is bucketed in UTC so the
// goldens do not depend on the machine's time zone.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const ccstats = path.resolve(process.argv[2] || 'D:/min/2026/ccstats');
const load = (p) => import(pathToFileURL(path.join(ccstats, p)));
const { listTranscripts, scanFile } = await load('src/scan.mjs');
const { aggregate } = await load('src/aggregate.mjs');
const { fromClaudeTuples } = await load('src/agents/adapters/claude.mjs');
const { aggregateAgents } = await load('src/agents/aggregate.mjs');

// Deterministic pseudo-random numbers (mulberry32).
let seed = 20260923;
const rnd = () => {
  seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const pick = (a) => a[Math.floor(rnd() * a.length)];
const int = (n) => Math.floor(rnd() * n);

const root = path.join(here, 'claude');
fs.rmSync(root, { recursive: true, force: true });
const models = ['claude-opus-4-8', 'claude-sonnet-4-6-20250929', 'claude-haiku-4-5-20251001', 'claude-fable-5-1',
  'claude-opus-5', 'anthropic.claude-sonnet-5', 'claude-experimental-9'];
const cwds = ['D:\\Work\\alpha', 'D:\\Work\\alpha\\sub', 'D:\\Work\\beta', '/home/dev/gamma', ''];
const start = Date.UTC(2026, 6, 28, 5, 0, 0);

function line(o) { return JSON.stringify(o); }
function assistant({ ts, session, req, msgId, model, usage, cwd, branch }) {
  return line({
    parentUuid: 'p', isSidechain: false, userType: 'external', cwd, sessionId: session, version: '2.1.0', gitBranch: branch,
    message: { id: msgId, type: 'message', role: 'assistant', model, content: [{ type: 'text', text: 'x'.repeat(int(40)) }], usage },
    requestId: req, type: 'assistant', uuid: 'u' + int(1e9), timestamp: new Date(ts).toISOString(),
  });
}

for (let s = 0; s < 14; s++) {
  const session = `sess-${String(s).padStart(3, '0')}`;
  const project = path.join(root, `project-${s % 4}`);
  fs.mkdirSync(project, { recursive: true });
  const lines = [];
  let ts = start + int(55) * 86400000 + int(24) * 3600000;
  const cwd = pick(cwds);
  const branch = pick(['main', 'dev', '']);
  for (let m = 0; m < 25; m++) {
    ts += int(40) * 60000 + int(60000);
    const model = rnd() < 0.8 ? models[s % models.length] : pick(models);
    const req = rnd() < 0.08 ? '' : `req_${s}_${m}`;
    const cw5 = int(3) ? int(20000) : 0;
    const cw1 = int(4) ? 0 : int(9000);
    const usage = {
      input_tokens: int(3000), cache_read_input_tokens: int(200000), output_tokens: int(4000),
      cache_creation_input_tokens: cw5 + cw1 + (rnd() < 0.1 ? int(500) : 0),
      cache_creation: rnd() < 0.15 ? undefined : { ephemeral_5m_input_tokens: cw5, ephemeral_1h_input_tokens: cw1 },
      output_tokens_details: rnd() < 0.5 ? { thinking_tokens: int(1000) } : undefined,
      service_tier: 'standard',
    };
    // Streaming snapshots and one copy per content block share the request id.
    const copies = req ? 1 + int(3) : 1;
    for (let c = 0; c < copies; c++) {
      const snapshot = { ...usage, output_tokens: c === copies - 1 ? usage.output_tokens : int(usage.output_tokens + 1) };
      lines.push(assistant({ ts: ts + c * 150, session, req, msgId: `msg_${s}_${m}`, model, usage: snapshot, cwd, branch }));
    }
    if (rnd() < 0.2) lines.push(line({ type: 'user', message: { role: 'user', content: 'usage please' }, toolUseResult: { usage: 1 }, timestamp: new Date(ts).toISOString(), sessionId: session, padding: 'y'.repeat(80) }));
    if (rnd() < 0.05) lines.push(assistant({ ts, session, req: 'synthetic', msgId: 'm', model: '<synthetic>', usage: { input_tokens: 0, output_tokens: 0 }, cwd, branch }));
  }
  if (s === 3) lines.push('{"type":"assistant","usage": broken json ' + 'z'.repeat(90));
  fs.writeFileSync(path.join(project, `${session}.jsonl`), lines.join('\n') + '\n');
}
// A sub-agent transcript nested under a session folder is counted too.
fs.mkdirSync(path.join(root, 'project-0', 'sess-000', 'subagents'), { recursive: true });
fs.writeFileSync(path.join(root, 'project-0', 'sess-000', 'subagents', 'agent-1.jsonl'),
  assistant({ ts: start + 3600000, session: 'sess-000', req: 'req_sub', msgId: 'msg_sub', model: 'claude-haiku-4-5', usage: { input_tokens: 10, output_tokens: 20, cache_read_input_tokens: 30, cache_creation_input_tokens: 40 }, cwd: 'D:\\Work\\alpha', branch: 'main' }) + '\n');

const records = [];
for (const f of listTranscripts([root])) records.push(...(await scanFile(f)));
const write = (name, value) => fs.writeFileSync(path.join(here, name), JSON.stringify(value, null, 1) + '\n');
write('claude-payload.golden.json', aggregate(records, { tz: 'utc' }));
write('claude-payload-raw.golden.json', aggregate(records, { tz: 'utc', dedupe: false }));
write('agents-payload.golden.json', aggregateAgents([{ id: 'claude', label: 'Claude Code', records: fromClaudeTuples(records) }], { title: 'All agents', tz: 'utc' }));
console.log(`${records.length} records`);
