export type PreflightMode = 'static' | 'trusted';

/** Capability names, compatible schema and successful report establish support; versions do not. */
export function preflightModes(value: unknown): PreflightMode[] {
  if (typeof value !== 'object' || value === null) return [];
  const report = value as Record<string, unknown>;
  if (report['success'] !== true || typeof report['schemaVersion'] !== 'string' ||
      !/^1(?:\.\d+)*$/.test(report['schemaVersion']) || !Array.isArray(report['capabilities'])) return [];
  const modes: PreflightMode[] = [];
  for (const [name, mode] of [['preflight-static-inputs', 'static'], ['preflight-trusted-catalog', 'trusted']] as const) {
    if (report['capabilities'].some(item => typeof item === 'object' && item !== null &&
        item.name === name && item.schemaVersion === '1.0' && (item.maturity === 'Preview' || item.maturity === 'Stable'))) modes.push(mode);
  }
  return modes;
}

/** The bundled server identity is independent of the selected project's Core. */
export function serverCapabilityText(value: unknown): string {
  if (typeof value !== 'object' || value === null) return 'Language server capabilities: unreported; optional features remain unavailable.';
  const result = value as { capabilities?: { experimental?: { lithosharp?: Record<string, unknown> } } };
  const report = result.capabilities?.experimental?.lithosharp;
  if (!report || report['schemaVersion'] !== '1.0') return 'Language server capabilities: unreported; optional features remain unavailable.';
  return `Language server bundled Core: ${typeof report['coreVersion'] === 'string' ? report['coreVersion'] : 'unknown'}; ` +
    `Markdown inspection ${report['markdownInspection'] === true ? 'available' : 'unavailable'}; ` +
    'MDX readiness is reported after actual analysis. Preflight uses the explicit CLI command; project Core is not acquired by initialize.';
}
