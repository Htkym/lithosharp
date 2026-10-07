import * as path from 'node:path';

/** Resolution never starts a process or reads workspace paths before trust. */
export function resolveLanguageServerCommand(
  isTrusted: boolean,
  explicitPath: string,
  extensionPath: string,
  isFile: (file: string) => boolean,
): string[] | undefined {
  if (!isTrusted) return undefined;
  const explicit = explicitPath.trim();
  if (explicit !== '') {
    return /\.dll$/i.test(explicit) ? ['dotnet', explicit] : [explicit];
  }
  const dll = path.join(extensionPath, 'resources', 'language-server', 'LithoSharp.LanguageServer.dll');
  return isFile(dll) ? ['dotnet', dll] : undefined;
}
