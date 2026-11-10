// Writes the invoice builder's ready-made designs (src/app/core/canvas.ts) to the mobile app, so both offer the same ones.
// Run after changing PRESETS:  npm run presets:mobile
import { build } from 'esbuild';
import { createRequire } from 'node:module';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const out = join(tmpdir(), `canvas-${Date.now()}.cjs`);
await build({ entryPoints: ['src/app/core/canvas.ts'], bundle: true, format: 'cjs', platform: 'node', outfile: out, logLevel: 'error' });
const { PRESETS } = createRequire(import.meta.url)(out);
const target = '../../mobile/fdms_mobile/assets/builder';
mkdirSync(target, { recursive: true });
writeFileSync(join(target, 'presets.json'), JSON.stringify(PRESETS.map(p => ({ key: p.key, name: p.name, hint: p.hint, swatch: p.swatch, layout: p.build() }))));
rmSync(out);
console.log(`wrote ${PRESETS.length} designs`);
