import * as fs from 'node:fs';
import * as path from 'node:path';

/** A LithoSharp project candidate found by static content. */
export interface ProjectCandidate {
  /** Workspace folder filesystem path used as the selection scope. */
  workspaceFolder: string;
  /** Workspace folder display name for disambiguation. */
  workspaceName: string;
  /** Absolute project file path. */
  projectPath: string;
  /** Project file name without extension. */
  displayName: string;
  /** How the project was recognized. Only LithoSharp references become candidates. */
  kind: 'lithosharp';
}

/** Stable selection key: workspace folder plus project path. Never the bare file name. */
export function keyOf(candidate: Pick<ProjectCandidate, 'workspaceFolder' | 'projectPath'>): string {
  return `${candidate.workspaceFolder}${'\0'}${candidate.projectPath}`;
}

/** Workspace-level flags read from static files. MSBuild imports and conditions are never evaluated. */
export interface WorkspaceFlags {
  centralManagement: boolean;
  toolManifest: boolean;
}

const IGNORED_SEGMENTS = new Set(['bin', 'obj', 'node_modules', '.git', '.vs']);
const MAX_FILES = 200;

/**
 * Finds LithoSharp project candidates under one workspace folder.
 * Reads csproj text for a LithoSharp reference; project evaluation never runs.
 */
export async function findProjectCandidates(workspaceFolder: string, workspaceName?: string): Promise<ProjectCandidate[]> {
  const name = workspaceName ?? path.basename(workspaceFolder);
  const found: ProjectCandidate[] = [];
  const files = await listFiles(workspaceFolder, 2);
  for (const file of files) {
    if (!file.endsWith('.csproj')) {
      continue;
    }
    let text: string;
    try {
      text = await fs.promises.readFile(file, 'utf8');
    } catch {
      continue;
    }
    if (!/LithoSharp/i.test(text)) {
      continue;
    }
    // Static reference shapes only: a full MSBuild evaluation never happens here.
    if (!/ProjectReference[^>]*LithoSharp/i.test(text) && !/PackageReference[^>]*LithoSharp/i.test(text)) {
      continue;
    }
    found.push({
      workspaceFolder,
      workspaceName: name,
      projectPath: file,
      displayName: path.basename(file, '.csproj'),
      kind: 'lithosharp',
    });
  }
  found.sort((a, b) => (a.projectPath < b.projectPath ? -1 : a.projectPath > b.projectPath ? 1 : 0));
  return found;
}

/** Reads central-management and tool-manifest flags from static files. */
export async function detectWorkspaceFlags(workspaceFolder: string): Promise<WorkspaceFlags> {
  const exists = async (name: string): Promise<boolean> => {
    try {
      await fs.promises.access(path.join(workspaceFolder, name));
      return true;
    } catch {
      return false;
    }
  };
  const [packages, build, manifest] = await Promise.all([
    exists('Directory.Packages.props'),
    exists('Directory.Build.props'),
    exists(path.join('.config', 'dotnet-tools.json')),
  ]);
  return { centralManagement: packages || build, toolManifest: manifest };
}

async function listFiles(root: string, maxDepth: number): Promise<string[]> {
  const out: string[] = [];
  const walk = async (dir: string, depth: number): Promise<void> => {
    if (out.length >= MAX_FILES) {
      return;
    }
    let entries: fs.Dirent[];
    try {
      entries = await fs.promises.readdir(dir, { withFileTypes: true });
    } catch {
      return;
    }
    entries.sort((a, b) => (a.name < b.name ? -1 : 1));
    for (const entry of entries) {
      if (IGNORED_SEGMENTS.has(entry.name)) {
        continue;
      }
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (depth < maxDepth) {
          await walk(full, depth + 1);
        }
      } else if (entry.isFile()) {
        out.push(full);
        if (out.length >= MAX_FILES) {
          return;
        }
      }
    }
  };
  await walk(root, 0);
  return out;
}
