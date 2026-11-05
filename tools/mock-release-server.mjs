// Local GitHub Releases API simulator for tools/update-e2e.ps1.
// Usage: node tools/mock-release-server.mjs <assets-dir> [port]
// First /releases/latest call reports a new version; subsequent calls report
// the current version, so the restarted app does not loop updating forever.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const root = process.argv[2];
const port = Number(process.argv[3] || 8791);
if (!root) { console.error('missing assets dir'); process.exit(2); }

let checks = 0;
const names = fs.readdirSync(root).filter(n =>
  n.endsWith('.zip') || n.endsWith('.msix') || n.endsWith('.sha256'));
const zip = names.find(n => n.endsWith('_x64_portable.zip'));
const msix = names.find(n => n.endsWith('.msix'));

const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  if (u.pathname.endsWith('/releases/latest')) {
    checks += 1;
    const tag = checks === 1 ? 'v9.9.9' : 'v1.0.1';
    const assets = [zip, zip && zip + '.sha256', msix, msix && msix + '.sha256']
      .filter(Boolean)
      .map(name => ({
        name,
        browser_download_url: `http://127.0.0.1:${port}/assets/${encodeURIComponent(name)}`,
      }));
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ tag_name: tag, assets }));
    console.log(`CHECK ${checks} -> ${tag}`);
    return;
  }
  if (u.pathname.startsWith('/assets/')) {
    const name = decodeURIComponent(u.pathname.slice('/assets/'.length));
    const file = path.join(root, name);
    if (!fs.existsSync(file)) { res.writeHead(404); res.end('missing'); return; }
    res.writeHead(200, {
      'content-type': 'application/octet-stream',
      'content-length': fs.statSync(file).size,
    });
    fs.createReadStream(file).pipe(res);
    return;
  }
  res.writeHead(404);
  res.end('not found');
});

server.listen(port, '127.0.0.1', () => console.log(`MOCK_READY ${port}`));
