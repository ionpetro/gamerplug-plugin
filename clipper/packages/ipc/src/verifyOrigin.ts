/**
 * Security gate for the localhost control socket. Medal's Electron WS server
 * rejects any connection that carries an `Origin` header, which blocks browsers
 * and malicious web pages from reaching the recorder control plane
 * (REPORT.md §1 "Transport"). We do the same: the real recorder is a native
 * process and never sends Origin.
 */

export interface ConnectionHeaders {
  origin?: string;
  [key: string]: string | string[] | undefined;
}

export interface VerifyResult {
  allowed: boolean;
  reason?: string;
}

/**
 * Returns whether an incoming connection should be accepted. Any present
 * `Origin` header (case-insensitive) is rejected.
 */
export function verifyConnection(headers: ConnectionHeaders): VerifyResult {
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === "origin") {
      const value = headers[key];
      if (value !== undefined && value !== "") {
        return { allowed: false, reason: "origin-header-present" };
      }
    }
  }
  return { allowed: true };
}
