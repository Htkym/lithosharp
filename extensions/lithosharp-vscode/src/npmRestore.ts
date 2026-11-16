import * as path from 'node:path';

/** Resolve before spawning: cmd.exe drops inherited PATH values over 8191 characters. */
export async function resolveNpmCiCommand(options: {
  platform: NodeJS.Platform;
  nodeExecutable: string;
  envPath: string;
  cwd: string;
  isFile: (file: string) => Promise<boolean>;
  readNpmPrefix: (node: string, script: string) => Promise<string>;
}): Promise<string[]> {
  const args = ['ci', '--ignore-scripts', '--no-audit', '--no-fund'];
  if (options.platform !== 'win32') return ['npm', ...args];
  const win = path.win32;
  const directories = options.envPath.split(';').filter(Boolean).map(dir => dir.replace(/^"(.*)"$/, '$1'));
  let npmDirectory: string | undefined;
  for (const directory of directories) {
    if (await options.isFile(win.resolve(options.cwd, directory, 'npm.cmd'))) {
      npmDirectory = win.resolve(options.cwd, directory);
      break;
    }
  }
  const configured = options.nodeExecutable.trim();
  const nodeName = configured || 'node.exe';
  let node: string | undefined;
  const adjacentNode = npmDirectory === undefined ? undefined : win.join(npmDirectory, 'node.exe');
  if (configured === '' && adjacentNode !== undefined && await options.isFile(adjacentNode)) {
    node = adjacentNode;
  } else if (win.isAbsolute(nodeName) || /[\\/]/.test(nodeName)) {
    const candidate = win.resolve(options.cwd, nodeName);
    if (await options.isFile(candidate)) node = candidate;
  } else {
    const names = win.extname(nodeName) === '' ? [nodeName + '.exe', nodeName] : [nodeName];
    for (const directory of directories) {
      for (const name of names) {
        const candidate = win.resolve(options.cwd, directory, name);
        if (await options.isFile(candidate)) { node = candidate; break; }
      }
      if (node) break;
    }
  }
  if (!node) {
    throw new Error(configured
      ? 'Configured lithosharp.nodeExecutable was not found. Select an existing Node executable and retry MDX worker restore.'
      : 'Node was not found on PATH. Install Node.js 24.13.0 or set lithosharp.nodeExecutable, then retry MDX worker restore.');
  }
  // Mirror the standard npm.cmd's adjacent Node and npm-prefix.js redirect, without cmd.
  const directory = npmDirectory ?? win.dirname(node);
  let cli = win.join(directory, 'node_modules/npm/bin/npm-cli.js');
  const prefixScript = win.join(directory, 'node_modules/npm/bin/npm-prefix.js');
  if (await options.isFile(prefixScript)) {
    const lines = (await options.readNpmPrefix(node, prefixScript)).split(/\r?\n/).filter(line => line !== '');
    const prefix = lines.at(-1);
    if (prefix !== undefined) {
      const redirected = win.resolve(options.cwd, prefix, 'node_modules/npm/bin/npm-cli.js');
      if (await options.isFile(redirected)) cli = redirected;
    }
  }
  if (await options.isFile(cli)) return [node, cli, ...args];
  if (npmDirectory !== undefined) {
    throw new Error('The npm.cmd selected from PATH has no npm CLI. Repair that Node/npm installation, then retry MDX worker restore.');
  }
  throw new Error('npm was not found beside Node or on PATH. Install npm with Node.js 24.13.0, then retry MDX worker restore.');
}
