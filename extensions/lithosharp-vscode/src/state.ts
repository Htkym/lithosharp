import type { ProjectCandidate } from './projectDetector.js';
import type { BuildResult, CliOperation } from './buildRunner.js';

export interface AnalysisStatus {
  uri: string;
  version: number;
  language: 'markdown' | 'mdx';
  state: 'unparsed' | 'pending' | 'ready' | 'unavailable';
  message: string;
  projectStatus?: string;
  schemaName?: string;
  schemaScope?: 'built-in-binding' | 'build-required' | 'unknown';
  projectId?: string;
  projectGeneration?: number;
  contextAcquiredAt?: string;
}

export interface OperationStatus {
  id: number;
  command: CliOperation;
  target: string;
  startedAt: string;
  finishedAt?: string;
  result?: BuildResult;
}

export function operationSummary(operation: OperationStatus): string {
  const result = operation.result;
  const outcome = result?.outcome ?? 'running';
  const count = result ? `${result.diagnostics.length} usable structured diagnostic(s)` : 'diagnostics pending';
  let summary = `${operation.command}: ${outcome}; target ${operation.target}; ${operation.finishedAt ?? operation.startedAt}; ${count}`;
  if (result) summary += `; process exit ${result.processExitCode ?? 'unreported'}`;
  const raw = result?.raw;
  const preflight = raw && typeof raw === 'object' ? (raw as Record<string, unknown>)['preflight'] : undefined;
  if (preflight && typeof preflight === 'object') {
    const report = preflight as Record<string, unknown>;
    for (const field of ['mode', 'stage', 'coverage', 'sourceCoverage']) {
      if (typeof report[field] === 'string') summary += `; ${field}: ${report[field]}`;
    }
    const deferred = report['deferredReasons'];
    if (Array.isArray(deferred)) for (const reason of deferred) if (typeof reason === 'string') summary += `\nDeferred: ${reason}`;
    const sources = report['sources'];
    if (Array.isArray(sources)) summary += `\nReported sources: ${sources.length}; individual source states are in Output details.`;
  }
  return summary;
}

/** Four independent facts. Acquisition time is provenance, not a freshness guarantee. */
export function editorPresentation(project: ProjectState, analysis: AnalysisStatus | undefined,
  context: 'unacquired' | 'acquired' | 'needs-refresh', operation?: OperationStatus, serve?: string): { text: string; detail: string } {
  const parsed = analysis?.state === 'ready' ? 'analysis complete' : analysis?.state === 'unavailable' ? 'analysis unavailable'
    : analysis?.state === 'pending' ? 'analysis pending' : 'not analysed';
  const language = analysis?.language === 'mdx' ? 'MDX' : analysis ? 'Markdown' : 'Document';
  const schema = analysis?.schemaScope === 'built-in-binding' ? 'Built-in binding supported; binding success is not implied.'
    : analysis?.schemaScope === 'build-required' ? 'Custom schema: check with Build.' : 'Inspection scope unknown.';
  const operationText = operation ? `${operation.command} ${operation.result?.outcome ?? 'running'}` : 'no explicit operation';
  return {
    text: `${statusText(project)} | ${parsed}${context === 'needs-refresh' ? ' | context needs refresh' : ''}${operation ? ` | ${operationText}` : ''}${serve ? ` | serve ${serve}` : ''}`,
    detail: [
      `${language} analysis: ${parsed}${analysis ? ` (version ${analysis.version})` : ''}. ${analysis?.message ?? ''}`,
      `Project information: ${context}; document resolution: ${analysis?.projectStatus ?? 'unknown'}.`,
      analysis?.projectId ? `Applied context: ${analysis.projectId}; generation ${analysis.projectGeneration ?? 'unknown'}; acquired ${analysis.contextAcquiredAt ?? 'unknown'}.`
        : 'Applied context: unreported for this document.',
      'Only detected changes invalidate the context; unmonitored inputs are not guaranteed current.',
      `Schema: ${analysis?.schemaName ?? 'unknown'}. ${schema}`,
      `Explicit operation: ${operation ? operationSummary(operation) : 'not run'}.`,
      'Next: Inspect to acquire project information; Build to check custom types and the configured site; Preflight to inspect its reported phase.',
      'Restore MDX Worker or Restart Language Server when analysis is unavailable. Build success does not establish that Quality ran.',
    ].join('\n'),
  };
}

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
