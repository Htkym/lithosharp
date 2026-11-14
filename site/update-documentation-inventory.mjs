import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';

const site = path.dirname(fileURLToPath(import.meta.url));
const repository = path.dirname(site);
const compare = (left, right) => left < right ? -1 : left > right ? 1 : 0;
const files = [];
function walk(directory, depth = 0) {
    if (depth > 4) return;
    for (const item of readdirSync(path.join(repository, directory), { withFileTypes: true })) {
        if (['bin', 'obj', 'node_modules', '.git', '.local', 'history'].includes(item.name)) continue;
        const relative = path.posix.join(directory, item.name);
        if (item.isDirectory()) walk(relative, depth + 1);
        else if (item.name.endsWith('.md')) files.push(relative);
    }
}
walk('docs');
walk('docs/history');
for (const directory of ['src', 'samples', 'extensions', 'templates']) walk(directory);
files.push('README.md', 'README.ja.md');
const entries = [];
for (const japanese of files.filter(file => file.endsWith('.ja.md') && path.posix.dirname(file) === 'docs'))
    assert(files.includes(japanese.replace(/\.ja\.md$/, '.md')), `Missing English public document: ${japanese}`);
for (const english of files.filter(file => !file.endsWith('.ja.md')).sort()) {
    const isGuide = english.startsWith('docs/') && path.posix.dirname(english) === 'docs';
    const isReadme = path.posix.basename(english) === 'README.md'
        && (english === 'README.md' || english.split('/').length === 3
            && ['src/', 'samples/', 'extensions/', 'templates/'].some(prefix => english.startsWith(prefix)));
    if (!isGuide && !isReadme) continue;
    const japanese = english.slice(0, -3) + '.ja.md';
    assert(existsSync(path.join(repository, japanese)), `Missing Japanese public document: ${english}`);
    const historical = english === 'docs/migration-2.0.md';
    const collection = historical ? 'history' : isGuide ? 'reference' : english === 'README.md' ? 'overview'
        : english.startsWith('samples/') ? 'samples' : 'packages';
    const id = isGuide ? path.posix.basename(english, '.md') : english === 'README.md' ? 'introduction'
        : path.posix.dirname(english).replaceAll('/', '-').replaceAll('.', '-').toLowerCase();
    const source = {};
    for (const [locale, relative] of [['en', english], ['ja', japanese]]) {
        const bytes = readFileSync(path.join(repository, relative));
        assert(!bytes.includes(Buffer.from('\r\n')), `Canonical review sources must use LF: ${relative}`);
        const title = bytes.toString('utf8').match(/^# (.+)$/m)?.[1];
        assert(title, `Missing document title: ${relative}`);
        source[locale] = { path: relative, title, sha256: createHash('sha256').update(bytes).digest('hex') };
    }
    entries.push({ collection, id, historical, source });
}
const apiContracts = [];
for (const directory of readdirSync(path.join(repository, 'src'), { withFileTypes: true }).filter(item => item.isDirectory()))
    for (const name of ['PublicAPI.Shipped.txt', 'PublicAPI.Unshipped.txt']) {
        const relative = path.posix.join('src', directory.name, name);
        if (!existsSync(path.join(repository, relative))) continue;
        const bytes = readFileSync(path.join(repository, relative));
        apiContracts.push({ ...receipt(relative),
            entries: bytes.toString('utf8').split(/\r?\n/).filter(line => line && !line.startsWith('#')).length });
    }
function receipt(relative) {
    const canonical = readFileSync(path.join(repository, relative), 'utf8').replace(/\r\n/g, '\n');
    return { path: relative, sha256: createHash('sha256').update(canonical).digest('hex') };
}
function guide(id) {
    const entry = entries.find(entry => entry.collection === 'reference' && entry.id === id);
    assert(entry, `Missing public guide: ${id}`);
    return { en: entry.source.en.path, ja: entry.source.ja.path };
}
const editorPath = 'extensions/lithosharp-vscode/package.json';
const editor = JSON.parse(readFileSync(path.join(repository, editorPath), 'utf8'));
const configurations = [editor.contributes.configuration].flat().flatMap(item => Object.entries(item.properties));
const templates = [];
function scanTemplates(directory) {
    for (const entry of readdirSync(path.join(repository, directory), { withFileTypes: true })) {
        const relative = path.posix.join(directory, entry.name);
        if (entry.isDirectory()) scanTemplates(relative);
        else if (relative.endsWith('/.template.config/template.json')) {
            const template = JSON.parse(readFileSync(path.join(repository, relative), 'utf8'));
            templates.push({ ...receipt(relative), identity: template.identity, shortName: template.shortName,
                symbols: Object.keys(template.symbols ?? {}).sort() });
        }
    }
}
scanTemplates('templates/LithoSharp.ProjectTemplates/content');
const diagnosticSources = [];
const diagnosticIds = new Set();
function scanDiagnostics(directory) {
    for (const entry of readdirSync(path.join(repository, directory), { withFileTypes: true })) {
        if (['bin', 'obj', 'node_modules'].includes(entry.name)) continue;
        const relative = path.posix.join(directory, entry.name);
        if (entry.isDirectory()) scanDiagnostics(relative);
        else if (entry.name.endsWith('.cs')) {
            const text = readFileSync(path.join(repository, relative), 'utf8');
            const ids = [...text.matchAll(/"((?:LS[A-Z]*|LIT|OKF)\d{3})"/g)].map(match => match[1]);
            if (ids.length) {
                diagnosticSources.push(receipt(relative));
                for (const id of ids) diagnosticIds.add(id);
            }
        }
    }
}
scanDiagnostics('src');
const cliPath = 'src/LithoSharp.Tool/Cli.cs';
const cli = readFileSync(path.join(repository, cliPath), 'utf8');
const help = cli.slice(cli.indexOf('private static void PrintHelp()'));
const sourceSurfaces = {
    cli: { source: receipt(cliPath), usage: [...help.matchAll(/^\s+lithosharp ([^\r\n]+)/gm)].map(match => match[1].trim()), guides: guide('cli') },
    editor: { source: receipt(editorPath), commands: editor.contributes.commands.map(command => command.command).sort(),
        settings: configurations.map(([id, definition]) => ({ id, type: definition.type, default: definition.default })).sort((a, b) => compare(a.id, b.id)), guides: guide('language-server') },
    templates: { entries: templates.sort((a, b) => compare(a.identity, b.identity)), guides: guide('distribution') },
    diagnostics: { ids: [...diagnosticIds].sort(), sources: diagnosticSources.sort((a, b) => compare(a.path, b.path)), guides: guide('diagnostics') }
};
const historicalSources = files.filter(file => file.startsWith('docs/history/')).sort().map(file => ({ ...receipt(file),
    disposition: 'Preserved historical source; linked to its repository archive without inventing a current-version variant.' }));
const inventory = { schemaVersion: 1, sourceVersion: '3.0', locales: ['en', 'ja'], implementationHashEncoding: 'utf8-with-lf',
    entries, historicalSources, apiContracts, sourceSurfaces };
const output = path.join(site, 'documentation-inventory.json');
const serialized = JSON.stringify(inventory, null, 2) + '\n';
if (process.argv.includes('--write')) writeFileSync(output, serialized);
else assert.equal(readFileSync(output, 'utf8'), serialized, 'Public documentation/API inventory changed; review the source pairs and explicitly regenerate with --write.');
console.log(`Public documentation inventory: ${entries.length} paired sources; ${apiContracts.length} API contract files.`);
