import { readFileSync, existsSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const site = path.dirname(fileURLToPath(import.meta.url));
function argument(name) {
    const index = process.argv.indexOf(name);
    assert(index >= 0 && process.argv[index + 1], `Pass ${name}.`);
    return path.resolve(process.argv[index + 1]);
}
const inputs = argument('--inputs');
const output = argument('--output');
const versionMatrix = JSON.parse(readFileSync(path.join(site, '../eng/version-matrix.json'), 'utf8'));
function verifyVersionBadges(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) verifyVersionBadges(file);
        else if (entry.name.endsWith('.html')) {
            const html = readFileSync(file, 'utf8');
            const header = html.match(/<header[\s\S]*?<\/header>/)?.[0];
            assert(header, `Missing site header: ${file}`);
            const relative = path.relative(output, file).replaceAll('\\', '/');
            // RenderDocs examples intentionally demonstrate the unmodified shared template.
            if (relative.startsWith('examples/') || relative.startsWith('knowledge/')) {
                assert(header.includes('class="docs-header"'), `Missing standard example header: ${file}`);
                continue;
            }
            const badges = [...header.matchAll(/<span class="website-version">([^<]+)<\/span>/g)];
            assert.equal(badges.length, 1, `Expected one version badge: ${file}`);
            assert.equal(badges[0][1], versionMatrix.lithosharpTarget, `Version badge mismatch: ${file}`);
        }
    }
}
verifyVersionBadges(output);
const metadata = JSON.parse(readFileSync(path.join(inputs, 'api/public-symbols.json'), 'utf8'));
const coverage = JSON.parse(readFileSync(path.join(inputs, 'api/api-coverage.json'), 'utf8'));
assert.equal(coverage.source, 'selected-package-assembly-metadata-and-compiler-xml');
let apiPages = 0;
for (const assembly of metadata.assemblies) {
    const variants = coverage.variants.filter(variant => variant.assembly === assembly.assembly && variant.collection === assembly.assembly);
    assert.deepEqual(variants.map(variant => variant.locale).sort(), ['en', 'ja']);
    for (const variant of variants) assert.deepEqual(variant.ids, assembly.ids, `Public API IDs differ: ${assembly.assembly}/${variant.locale}`);
}
for (const variant of coverage.variants) {
    const counterpart = coverage.variants.find(candidate => candidate.collection === variant.collection && candidate.assembly === variant.assembly && candidate.locale !== variant.locale);
    assert(counterpart, `Missing API counterpart: ${variant.collection}/${variant.locale}`);
    assert.deepEqual(variant.ids, counterpart.ids);
    assert.equal(variant.pages.length, variant.ids.length);
    for (const page of variant.pages) {
        const file = path.resolve(output, page.output);
        assert(file.startsWith(output + path.sep), `API output leaves its root: ${page.output}`);
        const html = readFileSync(file, 'utf8');
        assert(html.includes(`<html lang="${variant.locale}"`), `API language: ${page.output}`);
        assert(/<main\b[^>]*id="docs-main"/.test(html) && html.includes('href="#docs-main"'), `Missing API main landmark or skip target: ${page.output}`);
        assert(html.includes(variant.locale === 'ja' ? '本文へ移動' : 'Skip to main content'), `API skip link: ${page.output}`);
        if (variant.locale === 'ja') assert(!html.includes('>Skip to main content</a>'), `English API shell: ${page.output}`);
        assert(html.includes('website-search-form') && html.includes('type="search"'), `API site search: ${page.output}`);
        assert(html.includes('class="website-language"'), `API language control: ${page.output}`);
        const header = html.match(/<header[\s\S]*?<\/header>/)?.[0] ?? '';
        for (const language of ['en', 'ja']) assert(header.includes(`/api/${language}/`), `API counterpart header: ${page.output}/${language}`);
        apiPages++;
    }
}
const inventory = JSON.parse(readFileSync(path.join(site, 'documentation-inventory.json'), 'utf8'));
for (const entry of inventory.entries)
    for (const locale of ['en', 'ja']) {
        const file = path.join(output, entry.collection, locale, entry.id, 'index.html');
        assert(existsSync(file), `Missing canonical document: ${entry.source[locale].path}`);
        const html = readFileSync(file, 'utf8');
        assert(html.includes(`<html lang="${locale}"`), `Canonical document language: ${entry.source[locale].path}`);
    }
const notFound = readFileSync(path.join(output, '404.html'), 'utf8');
const fragmentBindings = [
    ['samples/ja/samples-lithosharp-docssample', 'c', 'C#の型定義から生成物の検査まで', 'cの型定義から生成物の検査まで'],
    ['reference/ja/Testing-v2-api', 'section-2', '移行', '移行'],
    ['history/ja/migration-2.0', 'docs', '既存Docsを型付きコンテンツへ移す', '既存docsを型付きコンテンツへ移す'],
    ['reference/ja/typed-markdown-collections', 'section-4', 'コードの取り込みと本文解析', 'コードの取り込みと本文解析'],
    ['reference/en/layout-css-contract', 'theme-switching', 'Theme switching', 'theme-switching--テーマ切り替え', true],
    ['reference/ja/layout-css-contract', 'section', 'テーマの切り替え', 'テーマ切り替え', true],
    ['reference/ja/quickstart', 'docsci', 'このDocsプロジェクトのCI', 'このdocsプロジェクトのci'],
    ['reference/ja/quickstart', 'section-3', '使用例と次の手順', '使用例と次の手順'],
    ['reference/ja/known-limitations', 'markdown', 'Markdown の互換性', 'markdown-の互換性'],
];
for (const [route, nativeId, heading, alias, declaredMarker] of fragmentBindings) {
    const html = readFileSync(path.join(output, route, 'index.html'), 'utf8');
    const ids = [...html.matchAll(/<[a-z][^>]*?\s+id="([^"]*)"/gi)].map(match => match[1]);
    assert.equal(ids.length, new Set(ids).size, `Duplicate document IDs: ${route}`);
    assert.equal(ids.filter(id => id === nativeId).length, 1, `Native heading ID changed: ${route}#${nativeId}`);
    assert.equal(ids.filter(id => id === alias).length, 1, `Missing or duplicate legacy fragment: ${route}#${alias}`);
    assert(html.includes(`<span id="${alias}" tabindex="-1"></span><h2 id="${nativeId}">${heading}</h2>`), `Legacy fragment is not attached to its unchanged heading: ${route}#${alias}`);
    if (declaredMarker)
        assert(!html.includes(`&lt;a id=&quot;${alias}&quot;&gt;&lt;/a&gt;`), `Escaped legacy marker remains visible: ${route}#${alias}`);
}
assert(notFound.includes('Page not found') && notFound.includes('ページが見つかりません'));
assert(notFound.includes('<section lang="ja">'));
assert(notFound.includes('name="robots" content="noindex,follow"'));
console.log(`Generated site coverage passed: ${apiPages} API pages, ${inventory.entries.length * 2} canonical guide pages and ${fragmentBindings.length} preserved fragment aliases. Browser/host checks are separate.`);

// Official shell contracts. Interactive keyboard/responsive checks run separately.
for (const locale of ['en', 'ja']) {
    const intro = readFileSync(path.join(output, locale, 'introduction/index.html'), 'utf8');
    assert(intro.includes('data-search-scope="site"'), `Site-wide search: ${locale}`);
    assert(intro.includes(`data-locale="${locale}"`), `Search locale: ${locale}`);
    assert(intro.includes('data-docs-menu-toggle'), `Sidebar toggle: ${locale}`);
    assert.equal((intro.match(/<summary>/g) ?? []).length >= 4, true, `Grouped navigation: ${locale}`);
    assert(intro.includes('website-language'), `Language control: ${locale}`);
    assert(intro.includes('data-website-search-clear'), `Clear search: ${locale}`);
    const tooling = readFileSync(path.join(output, locale, 'tooling/index.html'), 'utf8');
    assert(!tooling.includes('class="website-toc"'), `Empty TOC omitted: ${locale}`);
    assert(tooling.includes('rel="prev"') && tooling.includes('rel="next"'), `Adjacent page links: ${locale}`);
}
for (const file of ['index.html', '404.html', 'search.html']) {
    const html = readFileSync(path.join(output, file), 'utf8');
    assert(html.includes('class="docs-header"'), `Common utility header: ${file}`);
    assert(html.includes('website-search-form') && html.includes('type="search"'), `Site search entry: ${file}`);
    assert(html.includes('website-language'), `Language entry: ${file}`);
    assert(!html.includes('id="docs-sidebar"'), `No unrelated sidebar: ${file}`);
}
console.log('Official navigation, search and empty-TOC contracts passed.');
