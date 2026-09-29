import * as fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

const [configPath] = process.argv.slice(2);
if (!configPath) throw new Error('Usage: node Merge-MigrationRouteOracles.mjs <merge-config.json>');
const config = JSON.parse(await fs.readFile(configPath, 'utf8'));

const routesByPath = new Map();
const sourceMapByPath = new Map();
const inputs = [];
for (const input of config.inputs) {
  const oracleBytes = await fs.readFile(input.routeOraclePath);
  const sourceMapBytes = await fs.readFile(input.sourceMapPath);
  const oracle = JSON.parse(oracleBytes);
  const sourceMap = JSON.parse(sourceMapBytes);
  inputs.push({
    locale: input.locale,
    routeOraclePath: input.routeOraclePath,
    routeOracleSha256: createHash('sha256').update(oracleBytes).digest('hex'),
    routeSourceMapPath: input.sourceMapPath,
    routeSourceMapSha256: createHash('sha256').update(sourceMapBytes).digest('hex'),
  });
  for (const route of oracle.routes) {
    const previous = routesByPath.get(route.path);
    if (!previous) routesByPath.set(route.path, route);
    else if (JSON.stringify(previous) !== JSON.stringify(route)) {
      throw new Error(`Locale route inputs conflict for '${route.path}'.`);
    }
  }
  for (const entry of sourceMap.routes) {
    const previous = sourceMapByPath.get(entry.path);
    if (!previous) sourceMapByPath.set(entry.path, entry);
    else if (JSON.stringify(previous) !== JSON.stringify(entry)) {
      throw new Error(`Source-map locale inputs conflict for '${entry.path}'.`);
    }
  }
}

const routes = [...routesByPath.values()].sort((left, right) => left.path.localeCompare(right.path));
const sourceMapRoutes = [...sourceMapByPath.values()].sort((left, right) => left.path.localeCompare(right.path));
const oracle = {
  schemaVersion: '1.0',
  sourceVersion: config.sourceVersion,
  basePath: config.basePath,
  routes,
};
const sourceMap = {
  schemaVersion: '1.0',
  routeCount: sourceMapRoutes.length,
  routes: sourceMapRoutes,
};
await fs.mkdir(path.dirname(config.routeOracleOutput), { recursive: true });
await fs.mkdir(path.dirname(config.routeSourceMapOutput), { recursive: true });
await fs.writeFile(config.routeOracleOutput, `${JSON.stringify(oracle, null, 2)}\n`);
await fs.writeFile(config.routeSourceMapOutput, `${JSON.stringify(sourceMap, null, 2)}\n`);

console.log(JSON.stringify({
  siteId: config.siteId,
  inputBuilds: inputs,
  routeCount: routes.length,
  sourceDocumentMapCount: sourceMapRoutes.length,
  routeOracleSha256: createHash('sha256').update(await fs.readFile(config.routeOracleOutput)).digest('hex'),
  sourceMapSha256: createHash('sha256').update(await fs.readFile(config.routeSourceMapOutput)).digest('hex'),
}, null, 2));
