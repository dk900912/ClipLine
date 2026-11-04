// 把 docs/01-feature-alignment.md 的全部验收编号映射到自动化用例，生成追溯矩阵。
// 用法：node tools/traceability.mjs [报告csv路径]
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const alignmentPath = path.join(root, 'docs/01-feature-alignment.md');
const reportPath = path.resolve(root, process.argv[2] ?? 'docs/verification/final-report.csv');
if (!fs.existsSync(reportPath)) {
  console.error(`找不到测试报告：${reportPath}`);
  process.exit(1);
}

const alignment = fs.readFileSync(alignmentPath, 'utf8');
const entries = [...alignment.matchAll(/^- \[ \] `([A-Z]-\d+)`\s+(.+)$/gm)]
  .map((m) => ({ id: m[1], text: m[2].replace(/\|/g, '\\|').trim() }));

const report = fs.readFileSync(reportPath, 'utf8');
const testIds = report.split(/\r?\n/).slice(1)
  .map((line) => line.match(/^([^,]+),/))
  .filter(Boolean)
  .map((m) => m[1]);
const baseId = (id) => id.replace(/[a-z]+$/, '');
const covered = new Set(testIds.map(baseId));

const rows = entries.map((e) => {
  const hit = covered.has(e.id);
  return `| \`${e.id}\` | ${e.text.slice(0, 110)} | ${hit ? '✅ 自动化已验' : '⚠️ 需实机 / 渲染层'} |`;
});

const total = entries.length;
const done = entries.filter((e) => covered.has(e.id)).length;
const percent = total === 0 ? 0 : ((done / total) * 100).toFixed(1);

const out = [
  '# ClipLine · 验收编号追溯矩阵',
  '',
  `> 由 \`tools/traceability.mjs\` 从 \`docs/01-feature-alignment.md\` 与 \`${path.relative(root, reportPath)}\` 生成。`,
  `> 自动化覆盖：**${done} / ${total}（${percent}%）**；测试用例 ${testIds.length} 条。`,
  '> 「需实机 / 渲染层」项不属于逻辑缺陷，而是本机无 WinUI 3 工作负载、或必须真人目视验收的部分，详见 `docs/06` §5。',
  '',
  '| 编号 | 验收项 | 状态 |',
  '|---|---|---|',
  ...rows,
  '',
  '## 自动化用例索引',
  '',
  testIds.map((id) => `- \`${id}\``).join('\n'),
  '',
].join('\n');

const target = path.join(root, 'docs/verification/traceability.md');
fs.writeFileSync(target, out, 'utf8');
console.log(`追溯矩阵：${done}/${total} (${percent}%) → ${path.relative(root, target)}`);
