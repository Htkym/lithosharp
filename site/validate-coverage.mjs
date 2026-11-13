import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

const root = path.dirname(fileURLToPath(import.meta.url));
const repository = path.dirname(root);
const coverage = JSON.parse(readFileSync(path.join(root, 'feature-coverage.json'), 'utf8'));
assert.equal(coverage.schemaVersion, 1);
assert.equal(coverage.version, '3.0');
assert.deepEqual(coverage.locales, ['en', 'ja']);
assert.deepEqual(coverage.publicApiPackages, ['LithoSharp', 'LithoSharp.Mdx', 'LithoSharp.Images', 'LithoSharp.Testing']);
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const ids = new Set();
const pages = new Map();
for (const locale of coverage.locales) {
    const names = readdirSync(path.join(root, 'content', locale)).filter(name => name.endsWith('.md')).sort();
    pages.set(locale, names);
    for (const name of names) {
        const text = readFileSync(path.join(root, 'content', locale, name), 'utf8');
        assert(!text.includes('\r\n'), `Handbook review sources must use LF: ${locale}/${name}`);
        assert.match(text, /^---\r?\n[\s\S]*?supported_version: "3\.0"[\s\S]*?\r?\n---\r?\n/);
        assert(!/\b(?:T\d{2}|C0\d)\b/.test(text), `Internal plan ID in ${locale}/${name}`);
        for (const [, target] of text.matchAll(/\]\(([^:)#]+\.md)(?:#[^)]*)?\)/g))
            assert(existsSync(path.resolve(root, 'content', locale, target)), `Missing link: ${locale}/${name} -> ${target}`);
    }
}
assert.deepEqual(pages.get('en'), pages.get('ja'), 'Handbook IDs differ between locales');
for (const feature of coverage.features) {
    assert(!ids.has(feature.id), `Duplicate feature ID ${feature.id}`);
    ids.add(feature.id);
    assert(feature.publicEntries.length > 0 && feature.examples.length > 0);
    assert(['site', 'existing-sample', 'reference'].includes(feature.demonstration));
    for (const locale of coverage.locales) {
        assert(pages.get(locale).includes(feature.pages[locale]));
        assert(feature.constraints[locale].length > 0);
    }
    for (const source of feature.examples)
        assert(existsSync(path.join(repository, source)), `Missing example ${source}`);
}
const reviews = JSON.parse(readFileSync(path.join(root, 'translation-reviews.json'), 'utf8'));
const messages = JSON.parse(readFileSync(path.join(root, 'ui-messages.json'), 'utf8'));
assert.deepEqual(Object.keys(messages.en).sort(), Object.keys(messages.ja).sort());
assert.equal(messages.en.results.includes('{count}'), messages.ja.results.includes('{count}'));
assert.deepEqual(reviews.reviews.map(review => `${review.id}.md`).sort(), pages.get('en'));
for (const review of reviews.reviews) {
    assert.match(review.sourceSha256, /^[0-9a-f]{64}$/);
    assert.match(review.translationSha256, /^[0-9a-f]{64}$/);
    assert.equal(review.sourceSha256, sha256(readFileSync(path.join(root, 'content/en', review.id + '.md'))), `Stale English handbook review: ${review.id}`);
    assert.equal(review.translationSha256, sha256(readFileSync(path.join(root, 'content/ja', review.id + '.md'))), `Stale Japanese handbook review: ${review.id}`);
}
const inventory = JSON.parse(readFileSync(path.join(root, coverage.sourceInventory), 'utf8'));
const referenceReviews = JSON.parse(readFileSync(path.join(root, 'reference-translation-reviews.json'), 'utf8'));
assert.equal(referenceReviews.schemaVersion, 1);
assert.equal(referenceReviews.reviews.length, inventory.entries.length);
const reviewed = new Set();
for (const entry of inventory.entries) {
    const key = entry.collection + '/' + entry.id;
    const review = referenceReviews.reviews.find(review => review.collection === entry.collection && review.id === entry.id);
    assert(review, `Unreviewed canonical document: ${key}`);
    assert(!reviewed.has(key), `Duplicate canonical review: ${key}`);
    reviewed.add(key);
    for (const locale of ['en', 'ja']) {
        const source = entry.source[locale];
        const expectedHash = sha256(readFileSync(path.join(repository, source.path)));
        assert.equal(source.sha256, expectedHash, `Stale source inventory: ${source.path}`);
        assert.equal(review[locale === 'en' ? 'sourcePath' : 'translationPath'], source.path);
        assert.equal(review[locale === 'en' ? 'sourceSha256' : 'translationSha256'], expectedHash, `Stale canonical review: ${source.path}`);
    }
}
assert(inventory.sourceSurfaces.cli.usage.length > 0);
assert(inventory.sourceSurfaces.editor.commands.length > 0 && inventory.sourceSurfaces.editor.settings.length > 0);
assert.equal(inventory.sourceSurfaces.templates.entries.length, 4);
assert(inventory.sourceSurfaces.diagnostics.ids.length > 0);
const apiReviews = JSON.parse(readFileSync(path.join(root, 'api/translation-reviews.json'), 'utf8'));
for (const [pathKey, hashKey] of [['sourcePath', 'sourceSha256'], ['translationPath', 'translationSha256']])
    assert.equal(apiReviews.example[hashKey], sha256(readFileSync(path.join(root, apiReviews.example[pathKey]))), 'Example API translation review is stale.');
const project = readFileSync(path.join(root, 'LithoSharp.Website.csproj'), 'utf8');
assert(!project.includes('ProjectReference'));
assert.match(project, /<LithoSharpVersion Condition="[^"\n]+">3\.0\.0<\/LithoSharpVersion>/);
assert(!/(?:[A-Za-z]:\\|\/tmp\/)/.test(project), 'Absolute local feed in project');
console.log(`Coverage declarations valid: ${coverage.features.length} features; ${pages.get('en').length} paired handbook pages. Runtime verification is separate.`);
