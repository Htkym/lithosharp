import type { ProjectCandidate } from './projectDetector.js';

/** Project selection state shown in the status bar and output channel. */
export type ProjectState =
  | { kind: 'none'; candidates: number }
  | { kind: 'ambiguous'; candidates: number }
  | { kind: 'selected'; candidate: ProjectCandidate; cli: string; version: string | null }
  | { kind: 'untrusted' }
  | { kind: 'missing-cli'; reason: string };

/** Short status bar text. Never exposes paths beyond the project file name. */
export function statusText(state: ProjectState): string {
  switch (state.kind) {
    case 'none':
      return 'LithoSharp: no project';
    case 'ambiguous':
      return `LithoSharp: select project (${state.candidates})`;
    case 'selected':
      return `LithoSharp: ${state.candidate.displayName}${state.version ? ` ${state.version}` : ''}`;
    case 'untrusted':
      return 'LithoSharp: untrusted';
    case 'missing-cli':
      return 'LithoSharp: no CLI';
  }
}

/** QuickPick items keep workspace identity visible so same-name projects are never confused. */
export function quickPickItems(candidates: ProjectCandidate[]): Array<{
  label: string;
  description: string;
  detail: string;
  candidate: ProjectCandidate;
}> {
  return candidates.map((candidate) => ({
    label: candidate.displayName,
    description: candidate.workspaceName,
    detail: candidate.projectPath,
    candidate,
  }));
}
