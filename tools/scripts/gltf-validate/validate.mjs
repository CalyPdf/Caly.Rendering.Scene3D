// Runs the Khronos glTF-Validator over every .glb in a folder and prints, per file, the counts of errors,
// warnings, infos and hints, plus each distinct issue code with how often it occurs.
//
//   npm install            (once, in this folder)
//   node validate.mjs <folder with .glb files> [report.txt]
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const validator = createRequire(import.meta.url)('gltf-validator');
const folder = process.argv[2];
const reportPath = process.argv[3];
if (!folder) {
  console.error('usage: node validate.mjs <folder> [report.txt]');
  process.exit(1);
}

const lines = [`glTF-Validator ${validator.version()}`];
let totalErrors = 0;
for (const name of fs.readdirSync(folder).filter(f => f.toLowerCase().endsWith('.glb')).sort()) {
  const bytes = new Uint8Array(fs.readFileSync(path.join(folder, name)));
  const report = await validator.validateBytes(bytes, { uri: name, maxIssues: 0, writeTimestamp: false });
  const issues = report.issues;
  totalErrors += issues.numErrors;
  lines.push(`${name}: ${issues.numErrors} errors, ${issues.numWarnings} warnings, ${issues.numInfos} infos, ${issues.numHints} hints`);

  const byCode = new Map();
  for (const message of issues.messages) {
    const key = `${['ERROR', 'WARNING', 'INFO', 'HINT'][message.severity]} ${message.code}`;
    const entry = byCode.get(key) ?? { count: 0, first: `${message.pointer ?? ''} ${message.message}` };
    entry.count++;
    byCode.set(key, entry);
  }

  for (const [key, entry] of [...byCode].sort()) {
    lines.push(`    ${key} x${entry.count}   e.g. ${entry.first}`);
  }
}

lines.push(`total errors: ${totalErrors}`);
const text = lines.join('\n') + '\n';
process.stdout.write(text);
if (reportPath) {
  fs.writeFileSync(reportPath, text);
}

process.exit(totalErrors === 0 ? 0 : 2);
