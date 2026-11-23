type ErrorEnvelope = { error: { code: string; message: string } };
export const AUTH_SESSION_EXPIRED_EVENT = "ringfour:auth-session-expired";

const ACCESS_TOKEN_TIMEOUT_MS = 12_000;
const INTERACTIVE_AUTH_ERRORS = new Set([
  "login_required",
  "consent_required",
  "interaction_required",
  "invalid_grant",
  "missing_refresh_token",
]);

function authErrorCode(error: unknown): string | undefined {
  if (!error || typeof error !== "object") return undefined;
  const value = error as { error?: unknown; code?: unknown };
  const code = value.error ?? value.code;
  return typeof code === "string" ? code.toLowerCase() : undefined;
}

function requestInteractiveLogin() {
  window.dispatchEvent(new Event(AUTH_SESSION_EXPIRED_EVENT));
}

async function getAccessTokenOrRedirect(
  getAccessToken: () => Promise<string>,
): Promise<string> {
  let timeoutId: number | undefined;
  try {
    const token = await Promise.race([
      getAccessToken(),
      new Promise<never>((_, reject) => {
        timeoutId = window.setTimeout(
          () =>
            reject(
              Object.assign(new Error("Access token request timed out"), {
                code: "auth_token_timeout",
              }),
            ),
          ACCESS_TOKEN_TIMEOUT_MS,
        );
      }),
    ]);
    if (!token) throw new Error("No access token available");
    return token;
  } catch (error) {
    const code = authErrorCode(error);
    if (
      (code && INTERACTIVE_AUTH_ERRORS.has(code)) ||
      code === "auth_token_timeout"
    ) {
      requestInteractiveLogin();
    }
    throw error;
  } finally {
    if (timeoutId !== undefined) window.clearTimeout(timeoutId);
  }
}

// Calls the Platform API through the dev proxy (/api) with the caller's
// access token. Throws Error with the envelope message on non-2xx.
export async function apiFetch(
  getAccessToken: () => Promise<string>,
  path: string,
  init?: RequestInit,
): Promise<Response> {
  const token = await getAccessTokenOrRedirect(getAccessToken);
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: { ...init?.headers, Authorization: `Bearer ${token}` },
  });
  if (response.status === 401) requestInteractiveLogin();
  if (!response.ok) {
    let message = `Request failed with status ${response.status}`;
    try {
      const body = (await response.json()) as ErrorEnvelope;
      message = body.error.message;
    } catch {
      // keep generic message
    }
    throw new Error(message);
  }
  return response;
}

// Read every page so workspace selection and fleet navigation agree.
export async function apiFetchAll<T>(
  getAccessToken: () => Promise<string>,
  path: string,
): Promise<T[]> {
  const items: T[] = [];
  const seen = new Set<string>();
  let cursor: string | null | undefined = null;
  do {
    const url: string = cursor
      ? `${path}${path.includes("?") ? "&" : "?"}cursor=${encodeURIComponent(cursor)}`
      : path;
    const page = (await apiFetch(getAccessToken, url).then((response) =>
      response.json(),
    )) as { items: T[]; next_cursor?: string | null };
    items.push(...page.items);
    cursor = page.next_cursor;
    if (cursor) {
      if (seen.has(cursor) || seen.size >= 100)
        throw new Error("Unable to load all pages.");
      seen.add(cursor);
    }
  } while (cursor);
  return items;
}
