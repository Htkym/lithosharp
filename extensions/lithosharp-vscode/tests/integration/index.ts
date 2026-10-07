// VS Code integration test runner (V110-19). Executed inside the Extension
// Host by @vscode/test-electron with mocha.
import * as path from 'node:path';
import Mocha from 'mocha';
import { glob, writeFile } from 'node:fs/promises';

export async function run(): Promise<void> {
  const resultPath = process.env['LITHOSHARP_TEST_RESULT'];
  if (resultPath) await writeFile(resultPath, JSON.stringify({ started: true, finished: false }));
  const mocha = new Mocha({ ui: 'bdd', color: true, timeout: 60000, reporter: 'spec' });
  const testsRoot = path.resolve(__dirname, 'suite');
  console.log(`[lithosharp] integration tests root: ${testsRoot}`);
  const files: string[] = [];
  for await (const entry of glob('**/*.test.js', { cwd: testsRoot })) {
    files.push(path.join(testsRoot, entry as string));
  }
  console.log(`[lithosharp] integration files: ${JSON.stringify(files)}`);
  if (files.length === 0) throw new Error('No integration test files were discovered.');
  for (const file of files.sort()) {
    mocha.addFile(file);
  }
  await new Promise<void>((resolve, reject) => {
    const runner = mocha.run((failures) => {
      const stats = runner.stats;
      const result = { started: true, finished: true, tests: stats?.tests ?? 0, passes: stats?.passes ?? 0, failures };
      const save = resultPath ? writeFile(resultPath, JSON.stringify(result)) : Promise.resolve();
      void save.then(() => {
        if (failures > 0 || result.tests === 0) {
          reject(new Error(`${failures} integration tests failed; ${result.tests} executed.`));
        } else {
          resolve();
        }
      }, reject);
    });
  });
}
