import {createInterface} from 'node:readline';
import {format} from 'node:util';
import {compileSite} from './compiler.mjs';
import {readFile} from 'node:fs/promises';

const send = value => process.stdout.write(JSON.stringify(value) + '\n');
for (const method of ['log', 'info', 'warn', 'error', 'debug']) console[method] = (...args) => process.stderr.write(format(...args) + '\n');
const version = async name => JSON.parse(await readFile(new URL(`./node_modules/${name}/package.json`, import.meta.url), 'utf8')).version;
send({protocol: 1, type: 'ready', node: process.versions.node, mdx: await version('@mdx-js/mdx'), react: await version('react'), esbuild: await version('esbuild')});
for await (const line of createInterface({input: process.stdin, crlfDelay: Infinity})) {
  let message;
  try {
    if (Buffer.byteLength(line) > 128 * 1024 * 1024) throw new Error('Worker request exceeds its size limit.');
    message = JSON.parse(line);
    if (message.protocol !== 1 || message.type !== 'compile' || typeof message.requestId !== 'string') throw new Error('Unsupported worker protocol.');
    const result = await compileSite(message);
    send({protocol: 1, requestId: message.requestId, success: true, result});
  } catch (error) {
    const failures = error.errors ?? [error];
    send({protocol: 1, requestId: message?.requestId ?? '', success: false,
      requiredSources: [...new Set(failures.map(failure => failure.detail?.requiredSource ?? failure.requiredSource).filter(Boolean))],
      diagnostics: failures.map(failure => ({id: 'LSMDX001', message: failure.text ?? failure.message ?? String(failure),
        file: failure.location?.file ?? failure.file ?? '', line: failure.location?.line ?? failure.line ?? 1,
        column: (failure.location?.column ?? ((failure.column ?? 1) - 1)) + 1}))});
  }
}
