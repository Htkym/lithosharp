import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {access, mkdir, readFile, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {runInNewContext} from 'node:vm';

const usage = 'node --expose-gc eng/Measure-SearchRuntime.mjs --output <new-report.json> [--size 100|1000|10000 --seed 20261113 | --index <scoped.json> --collection <id> --version <version> --locale <locale>] [--query-policy fixture|core-browser] [--iterations 5 --warmup 1]';
if (process.argv.includes('--help')) { console.log(usage); process.exit(0); }
const options = new Map(), allowed = ['--output', '--size', '--seed', '--index', '--collection', '--version', '--locale', '--iterations', '--warmup', '--query-policy'];
const args = process.argv.slice(2);
for (let index = 0; index < args.length; index += 2) {
  if (!allowed.includes(args[index]) || args[index + 1] === undefined || options.has(args[index]))
    throw new Error(usage);
  options.set(args[index], args[index + 1]);
}
if (!options.get('--output')) throw new Error(usage);
const queryPolicy = options.get('--query-policy') ?? 'fixture';
if (!['fixture', 'core-browser'].includes(queryPolicy)) throw new Error('Unknown --query-policy.');
if (process.versions.node !== '24.13.0') throw new Error('Use the locked Node version 24.13.0.');
if (typeof global.gc !== 'function') throw new Error('Use --expose-gc to record explicitly requested GC boundaries.');
const integer = (name, fallback, minimum, maximum) => {
  const value = Number(options.get(name) ?? fallback);
  if (!Number.isSafeInteger(value) || value < minimum || value > maximum) throw new Error('Invalid ' + name);
  return value;
};
const iterations = integer('--iterations', 5, 1, Number.MAX_SAFE_INTEGER);
const warmup = integer('--warmup', 1, 0, Number.MAX_SAFE_INTEGER);
const seed = integer('--seed', 20261113, 1, 0xffffffff);
const actual = options.has('--index'), output = path.resolve(options.get('--output'));
if (actual && (options.has('--size') || options.has('--seed'))) throw new Error('Actual input is not synthetic scale.');
if (!actual && ['--collection', '--version', '--locale'].some(name => options.has(name))) throw new Error('Synthetic fixtures retain their original scopes.');
if (actual && ['--collection', '--version', '--locale'].some(name => !options.get(name))) throw new Error('Declare all three fields of the actual scoped index.');
try { await access(output); throw new Error('Use a new output file.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const runnerFile = fileURLToPath(import.meta.url);
const queryFile = fileURLToPath(new URL('../src/LithoSharp/Search/query.mjs', import.meta.url));
const fixtureFile = fileURLToPath(new URL('../tests/LithoSharp.Tests/Fixtures/Search/technical-search.json', import.meta.url));
const [runnerBytes, queryBytes, fixtureBytes] = await Promise.all([readFile(runnerFile), readFile(queryFile), readFile(fixtureFile)]);
const fixture = JSON.parse(fixtureBytes.toString('utf8'));
const queryDocuments = runInNewContext(queryBytes.toString('utf8') + '\nqueryDocuments;', Object.create(null), {filename: queryFile});
const documentsOf = value => Array.isArray(value) ? value : value.documents;
const validate = documents => {
  assert.ok(Array.isArray(documents) && documents.length > 0, 'Expected nonempty search documents.');
  const urls = new Set(), ids = new Set();
  for (const document of documents) {
    assert.equal(typeof document.title, 'string');
    assert.equal(typeof document.url, 'string');
    assert.ok(document.url.length > 0 && !urls.has(document.url), 'Document URLs must be unique exact strings.');
    urls.add(document.url);
    if (document.id !== undefined) {
      assert.equal(typeof document.id, 'string');
      assert.ok(!ids.has(document.id), 'Document IDs must be unique exact strings.');
      ids.add(document.id);
    }
    if (actual) for (const field of ['collection', 'version', 'locale'])
      assert.equal(document[field], options.get('--' + field), 'Input is not the declared scoped index: ' + field);
  }
};
// Expected results are verified only against the unchanged fixed fixture, outside timed intervals.
const fixtureIdentity = digest(JSON.stringify(fixture.documents));
for (const example of fixture.cases)
  assert.deepEqual(Array.from(queryDocuments(fixture.documents, example.query, example), hit => hit.url), example.expected, example.query);
assert.equal(digest(JSON.stringify(fixture.documents)), fixtureIdentity, 'Fixture reference identities changed.');

let inputBytes, inputPath = null, synthetic = null;
if (actual) {
  inputPath = path.resolve(options.get('--index'));
  inputBytes = await readFile(inputPath);
} else {
  const size = integer('--size', 100, 1, 10000);
  if (![100, 1000, 10000].includes(size)) throw new Error('--size must be 100, 1000 or 10000.');
  let state = seed;
  const documents = Array.from({length: size}, (_, index) => {
    state ^= state << 13; state ^= state >>> 17; state ^= state << 5; state >>>= 0;
    const original = fixture.documents[state % fixture.documents.length];
    // Keep identifiers and anchors verbatim; a distinct prefix makes every clone a separate URL.
    return {...original, id: 'synthetic:' + seed + ':' + index,
      url: '/__search_scale/' + seed + '/' + index + original.url,
      tags: [...original.tags], ...(original.sections ? {sections: original.sections.map(section => ({...section}))} : {})};
  });
  inputBytes = Buffer.from(JSON.stringify(documents), 'utf8');
  synthetic = {size, seed, algorithm: 'xorshift32 fixture sampling with replacement; exact unique URL prefix and ID',
    expectedRanking: 'unknown for scaled clones; fixed original fixture expectations verified separately'};
}
const inputText = inputBytes.toString('utf8');
const scope = actual ? Object.fromEntries(['collection', 'version', 'locale'].map(field => [field, options.get('--' + field)])) : null;
let policySource = {path: fixtureFile, sha256: digest(fixtureBytes), contract: 'Each fixture case fuzzy/limit; actual input overrides only collection/version/locale.'};
if (queryPolicy === 'core-browser') {
  const file = fileURLToPath(new URL('../src/LithoSharp/SiteGenerator.cs', import.meta.url));
  const bytes = await readFile(file);
  const contract = 'queryDocuments(scopedDocs, query, {fuzzy: true, limit: Number.MAX_SAFE_INTEGER})';
  const line = bytes.toString('utf8').split(/\r?\n/u).findIndex(value => value.includes(contract));
  assert.ok(line >= 0, 'Core query contract changed; review this policy before measuring.');
  policySource = {path: file, sha256: digest(bytes), line: line + 1, contract};
}
const cases = fixture.cases.flatMap((example, index) => {
  const query = queryPolicy === 'core-browser' ? example.query.trim() : example.query;
  if (queryPolicy === 'core-browser' && !query) return []; // Core bypasses the kernel for blank queries.
  return [{index, query, options: queryPolicy === 'core-browser' ? {fuzzy: true, limit: Number.MAX_SAFE_INTEGER}
    : {...(scope ?? {collection: example.collection, version: example.version, locale: example.locale}),
      fuzzy: example.fuzzy ?? false, ...(example.limit === undefined ? {} : {limit: example.limit})}}];
});
const report = {
  schemaVersion: 1, status: 'running', acceptance: 'not-evaluated', benchmark: 'search-runtime-node',
  comparison: 'candidate-only-search-feature-cost',
  queryPolicy: {name: queryPolicy, source: policySource, skippedBlankCaseCount: fixture.cases.length - cases.length},
  inputKind: actual ? 'actual-scoped-index' : 'synthetic-scale-from-fixed-fixture', inputPath, scope, synthetic,
  node: process.version, versions: process.versions, platform: process.platform, architecture: process.arch,
  processId: process.pid, arguments: args, nodeArguments: process.execArgv,
  runner: {path: runnerFile, sha256: digest(runnerBytes)}, source: {path: queryFile, sha256: digest(queryBytes)},
  fixture: {path: fixtureFile, sha256: digest(fixtureBytes), documents: fixture.documents.length,
    cases: fixture.cases.length, expectedResults: 'passed outside timed intervals on original fixture only'},
  input: {sha256: digest(inputBytes), utf8Bytes: inputBytes.length, retainedOutsideIntervals: 'input UTF8 Buffer and decoded JSON text remain live'},
  iterations, warmup, cases, samples: [],
  measurementScope: {
    parse: 'JSON.parse only; file I/O, UTF8 decoding, validation, GC and fixture creation are excluded',
    query: 'queryDocuments only with the recorded policy/options: fixture retains case fuzzy/limit; core-browser uses fuzzy=true and MAX_SAFE_INTEGER limit over the supplied documents. Core query trimming and blank-query bypass are outside the timer. Core UI prepare/tag filtering and UI result slicing/projection, DOM and transfer are not measured',
    memory: 'process.memoryUsage snapshots after explicit global.gc; no peak or allocation measurement. Signed heap deltas include runtime/instrumentation and are observations, not exact object sizes.',
    resultRetention: 'Only query result URLs/counts remain in samples; parsed documents and result objects are released between iterations.',
    timing: 'Raw intervals only; no cutoff, regression threshold, speedup or automatic acceptance verdict'
  },
  browser: {responseMilliseconds: null, initialTransferredBytes: null, retainedHeapBytes: null, status: 'unknown-not-measured-in-node'}
};
let failed;
try {
  for (let iteration = -warmup; iteration < iterations; iteration++) {
    // Function scope releases the decoded index before the next iteration's initial collection.
    const sample = (() => {
      global.gc();
      const beforeParse = process.memoryUsage();
      const parseStart = performance.now();
      const parsed = JSON.parse(inputText);
      const parseMilliseconds = performance.now() - parseStart;
      const documents = documentsOf(parsed);
      validate(documents);
      global.gc();
      const afterParse = process.memoryUsage();
      const beforeQueriesHash = digest(JSON.stringify(documents)), queries = [];
      for (const example of cases) {
        const queryStart = performance.now();
        const hits = queryDocuments(documents, example.query, example.options);
        const elapsedMilliseconds = performance.now() - queryStart;
        for (const hit of hits) {
          assert.ok(documents.includes(hit.document), 'Search returned a replacement document identity.');
          assert.ok(hit.url === hit.document.url || hit.url.startsWith(hit.document.url + '#'), 'Search changed the exact document URL.');
          assert.ok(hit.section == null || hit.document.sections?.includes(hit.section), 'Search returned a replacement section identity.');
          for (const field of ['collection', 'version', 'locale'])
            if (example.options[field] != null) assert.equal(hit.document[field], example.options[field], 'Search scope leak.');
        }
        queries.push({caseIndex: example.index, elapsedMilliseconds, resultCount: hits.length,
          urls: Array.from(hits, hit => hit.url), expectedResults: 'unknown for this measured corpus'});
      }
      assert.equal(digest(JSON.stringify(documents)), beforeQueriesHash, 'Search mutated input identities or content.');
      global.gc();
      const afterQueries = process.memoryUsage();
      return {iteration, warmup: iteration < 0, documentCount: documents.length, parseMilliseconds,
        beforeParse, afterParse, afterQueries,
        decodedHeapUsedDeltaBytes: afterParse.heapUsed - beforeParse.heapUsed,
        afterQueriesHeapUsedDeltaBytes: afterQueries.heapUsed - afterParse.heapUsed, queries};
    })();
    report.samples.push(sample);
  }
  report.status = 'completed';
} catch (error) {
  report.status = 'failed';
  report.error = {name: error.name, message: error.message};
  failed = error;
}
await mkdir(path.dirname(output), {recursive: true});
await writeFile(output, JSON.stringify(report, null, 2) + '\n', {flag: 'wx'});
console.log(output);
if (failed) throw failed;
