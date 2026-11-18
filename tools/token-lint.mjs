#!/usr/bin/env node
// Design-token lint: fails on raw hex/rgb colour literals in XAML outside
// Themes/PurgeTokens.xaml. All colours must come from the token dictionary.
//
//   node tools/token-lint.mjs
//
// Legacy files that predate the tokens are grandfathered (listed below) until
// the shell wave migrates them to tokens; any OTHER file with a raw literal
// fails the build. New XAML must use {StaticResource Purge*Brush} resources.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const srcDir = path.join(root, 'src', 'PurgeWindows');

// Files allowed to keep raw literals until the shell wave migrates them.
// PurgeTokens.xaml itself is exempt by definition (it DEFINES the literals).
const grandfathered = new Set([
  'Themes/Styles.xaml',
  'MainWindow.xaml',
  'Views/SettingsWindow.xaml',
  'Controls/LogoMark.xaml',
  'Views/ShareCard.xaml',
]);

const literal = /(#[0-9A-Fa-f]{3,8}\b|rgba?\s*\()/;

function* xamlFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* xamlFiles(full);
    else if (entry.name.endsWith('.xaml')) yield full;
  }
}

const failures = [];
for (const file of xamlFiles(srcDir)) {
  const rel = path.relative(srcDir, file).split(path.sep).join('/');
  if (rel === 'Themes/PurgeTokens.xaml' || grandfathered.has(rel)) continue;
  const lines = fs.readFileSync(file, 'utf8').split('\n');
  lines.forEach((line, i) => {
    if (literal.test(line)) failures.push(`${rel}:${i + 1}: ${line.trim()}`);
  });
}

if (failures.length > 0) {
  console.error('token-lint: raw colour literals outside Themes/PurgeTokens.xaml:');
  for (const f of failures) console.error(`  ${f}`);
  console.error('Use {StaticResource Purge*Brush} resources instead.');
  process.exit(1);
}
console.log('token-lint: clean');
