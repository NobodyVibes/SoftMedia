// Deploys the built client (dist/) into the server's wwwroot so the server hosts the
// SPA single-origin (which is what makes its security headers, incl. the CSP, actually
// apply to the app). Run via `npm run deploy:server`.
//
// Contract:
//  - wwwroot/cache is RUNTIME DATA (posters, trickplay, subtitles) — never touched.
//  - Everything else the previous deploy put in wwwroot is replaced wholesale (stale
//    hashed assets would otherwise accumulate forever), tracked via a manifest file so
//    only files a deploy created are ever deleted.
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const clientRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(clientRoot, 'dist');
const wwwroot = resolve(clientRoot, '..', 'SoftMedia.Server', 'wwwroot');
const manifestPath = join(wwwroot, '.spa-deploy-manifest.json');

if (!existsSync(join(dist, 'index.html'))) {
    console.error('dist/index.html not found — run `npm run build` first (deploy:server does this for you).');
    process.exit(1);
}

// Remove the previous deploy's files (manifest-listed only — cache/ and anything a
// human placed in wwwroot survive).
if (existsSync(manifestPath)) {
    const previous = JSON.parse(readFileSync(manifestPath, 'utf8'));
    for (const rel of previous.files ?? []) {
        if (rel.startsWith('cache')) continue; // belt & braces — never under cache/
        rmSync(join(wwwroot, rel), { force: true });
    }
    for (const rel of (previous.dirs ?? []).sort((a, b) => b.length - a.length)) {
        if (rel.startsWith('cache')) continue;
        try { rmSync(join(wwwroot, rel), { recursive: false }); } catch { /* not empty → keep */ }
    }
}

// Copy dist → wwwroot, recording every file/dir for the next deploy's cleanup.
const files = [];
const dirs = [];
function copyTree(fromDir, relBase) {
    for (const entry of readdirSync(fromDir)) {
        const from = join(fromDir, entry);
        const rel = relBase ? join(relBase, entry) : entry;
        if (statSync(from).isDirectory()) {
            dirs.push(rel);
            mkdirSync(join(wwwroot, rel), { recursive: true });
            copyTree(from, rel);
        } else {
            files.push(rel);
            cpSync(from, join(wwwroot, rel));
        }
    }
}
mkdirSync(wwwroot, { recursive: true });
copyTree(dist, '');
writeFileSync(manifestPath, JSON.stringify({
    deployedAt: new Date().toISOString(),
    files: files.map(f => f.replaceAll('\\', '/')),
    dirs: dirs.map(d => d.replaceAll('\\', '/')),
}, null, 2));

console.log(`Deployed ${files.length} files to ${relative(process.cwd(), wwwroot)} (cache/ untouched).`);
