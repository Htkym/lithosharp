import * as fs from 'node:fs';
import * as path from 'node:path';

/** Repository fixture directory, found by walking up to the extension package root. */
export function fixturePath(...segments: string[]): string {
  let dir = __dirname;
  for (;;) {
    if (fs.existsSync(path.join(dir, 'package.json'))) {
      return path.join(dir, 'tests', 'fixtures', ...segments);
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      throw new Error('Extension package root not found.');
    }
    dir = parent;
  }
}
