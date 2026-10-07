/** Thrown when an action needs a trusted workspace. Never executes anything. */
export class UntrustedWorkspaceError extends Error {
  constructor(action: string) {
    super(
      `LithoSharp cannot ${action} in an untrusted workspace. ` +
        `Trust the workspace first; project detection stays available without trust.`,
    );
    this.name = 'UntrustedWorkspaceError';
  }
}

/**
 * Gates an executing action on workspace trust. Call this first in every
 * command handler that could start a process, including indirect ones.
 */
export function requireTrusted(isTrusted: boolean, action: string): void {
  if (!isTrusted) {
    throw new UntrustedWorkspaceError(action);
  }
}
