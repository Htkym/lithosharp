import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';

const site = path.dirname(fileURLToPath(import.meta.url));
const directory = path.join(site, 'api', 'ja');
const reviews = JSON.parse(readFileSync(path.join(site, 'api/translation-reviews.json'), 'utf8'));
assert.equal(reviews.schemaVersion, 1);
const reviewedFiles = new Map(reviews.files.map(file => [file.path, file.sha256]));
const translated = new Map();
for (const name of readdirSync(directory).filter(name => /^units-.+\.json$/.test(name)).sort()) {
    const bytes = readFileSync(path.join(directory, name));
    assert(!bytes.includes(Buffer.from('\r\n')), `API review sources must use LF: ${name}`);
    assert.equal(reviewedFiles.get('ja/' + name), createHash('sha256').update(bytes).digest('hex'), `API translation review is stale: ${name}`);
    const document = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
    assert.equal(document.schemaVersion, 1);
    for (const [hash, unit] of Object.entries(document.units)) {
        assert.equal(hash, createHash('sha256').update(unit.english).digest('hex'), `English hash changed: ${hash}`);
        assert(!translated.has(hash), `Duplicate translation unit: ${hash}`);
        const formatName = unit.english === 'PNG.' && unit.japanese === 'PNG。';
        assert(typeof unit.japanese === 'string' && (formatName || /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}]/u.test(unit.japanese)), `Missing Japanese explanation: ${hash}`);
        const placeholders = text => [...text.matchAll(/\{\d+\}/g)].map(match => match[0]).sort();
        assert.deepEqual(placeholders(unit.english), placeholders(unit.japanese), `Inline XML changed: ${hash}`);
        translated.set(hash, unit);
    }
}
const index = process.argv.indexOf('--material');
assert(index >= 0 && process.argv[index + 1], 'Pass --material from an actual public-metadata/compiler-XML export.');
const source = JSON.parse(readFileSync(process.argv[index + 1], 'utf8').replace(/^\uFEFF/, ''));
assert.equal(source.schemaVersion, 1);
const missing = [];
for (const [hash, unit] of Object.entries(source.units)) {
    if (!translated.has(hash)) missing.push({ hash, ...unit });
    else assert.equal(translated.get(hash).english, unit.english);
}
if (missing.length) {
    console.error(JSON.stringify({ missingJapaneseDocumentation: missing }, null, 2));
    process.exitCode = 1;
} else console.log(`Japanese API coverage passed: ${Object.keys(source.units).length} actual prose units; ${translated.size} reviewed translations loaded.`);
