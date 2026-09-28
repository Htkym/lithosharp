// Fake LithoSharp CLI for controller tests. Speaks the machine protocol
// (JSON Lines on stdout, human logs on stderr) without building anything.
import * as net from 'node:net';
import * as readline from 'node:readline';

const mode = process.env['FAKE_MODE'] ?? 'normal';
const send = (value) => process.stdout.write(JSON.stringify(value) + '\n');
const fail = (message) => {
  process.stderr.write(`fake-cli: ${message}\n`);
};

if (mode === 'crash') {
  process.exit(3);
}

if (mode === 'fail-startup') {
  send({ schemaVersion: '1.0', event: 'startup-failed', success: false, exitCode: 1, error: 'Port 9999 is occupied.', outputDirectory: null });
  process.exit(1);
}

if (mode === 'silent') {
  // Never speaks: the controller must time out on its own.
  setInterval(() => {}, 1000);
  process.stdin.resume();
} else {
  await serve();
}

async function serve() {

if (mode === 'invalid-json') {
  process.stdout.write('this is not json\n');
}

const listen = () => new Promise((resolve) => {
  const server = net.createServer();
  server.listen(Number(process.env['FAKE_PORT'] ?? 0), '127.0.0.1', () => {
    resolve(server);
  });
});

const server = await listen();
const port = server.address().port;
if (mode === 'huge-stderr') {
  process.stderr.write(`x`.repeat(10 * 1024 * 1024));
}
if (mode === 'slow-startup') {
  await new Promise((resolve) => setTimeout(resolve, Number(process.env['FAKE_DELAY_MS'] ?? 500)));
}
send({
  schemaVersion: '1.0', event: 'startup', host: '127.0.0.1', requestedPort: 0, actualPort: port,
  url: `http://127.0.0.1:${port}`, outputDirectory: '/tmp/fake-out', basePath: '/',
  siteBasePath: '/', generation: 1, routes: [],
});
fail('listening');

let generation = 1;
const reader = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
const shutdown = () => {
  send({ schemaVersion: '1.0', event: 'shutdown', generation, exitCode: 0 });
  server.close();
  process.exit(0);
};
reader.on('line', (line) => {
  if (line.trim() === '') {
    return;
  }
  try {
    const message = JSON.parse(line);
    if (message.command === 'shutdown') {
      shutdown();
    }
  } catch {
    fail('bad control line');
  }
});
reader.on('close', () => {
  shutdown();
});
}
