// VS Code integration test runner (V110-19). Executed inside the Extension
// Host by @vscode/test-electron with mocha.
import * as path from 'node:path';
import Mocha from 'mocha';
import { glob } from 'node:fs/promises';

export async function run(): Promise<void> {
  const mocha = new Mocha({ ui: 'bdd', color: true, timeout: 60000, reporter: 'spec' });
  const testsRoot = path.resolve(__dirname, 'suite');
  console.log(`[lithosharp] integration tests root: ${testsRoot}`);
  const files: string[] = [];
  for await (const entry of glob('**/*.test.js', { cwd: testsRoot })) {
    files.push(path.join(testsRoot, entry as string));
  }
  console.log(`[lithosharp] integration files: ${JSON.stringify(files)}`);
  for (const file of files.sort()) {
    mocha.addFile(file);
  }
  await new Promise<void>((resolve, reject) => {
    mocha.run((failures) => {
      if (failures > 0) {
        reject(new Error(`${failures} integration tests failed.`));
      } else {
        resolve();
      }
    });
  });
}
