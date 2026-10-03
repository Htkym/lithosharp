import {createInterface} from 'node:readline';
import {format} from 'node:util';
import {compileSite, analyzeMdx} from './compiler.mjs';
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
    if (message.protocol !== 1 || (message.type !== 'compile' && message.type !== 'analyze') || typeof message.requestId !== 'string') throw new Error('Unsupported worker protocol.');
    const result = message.type === 'compile' ? await compileSite(message) : await analyzeMdx(message);
    send({protocol: 1, requestId: message.requestId, success: true, result});
  } catch (error) {
    const failures = error.errors ?? [error];
    send({protocol: 1, requestId: message?.requestId ?? '', success: false,
      requiredSources: [...new Set(failures.map(failure => failure.detail?.requiredSource ?? failure.requiredSource).filter(Boolean))],
      diagnostics: failures.map(failure => {
        const message = failure.text ?? failure.message ?? String(failure);
        const sourcePosition = /\((\d+):(\d+)-\d+:\d+\)\s*$/.exec(message);
        return {id: 'LSMDX001', message,
          file: failure.location?.file ?? failure.file ?? '',
          line: sourcePosition ? Number(sourcePosition[1]) : failure.location?.line ?? failure.line ?? null,
          column: sourcePosition ? Number(sourcePosition[2]) : Number.isInteger(failure.location?.column)
            ? failure.location.column + 1
            : (Number.isInteger(failure.column) ? failure.column : null)};
      })});
  }
}
