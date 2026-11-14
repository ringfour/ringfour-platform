type ErrorEnvelope = { error: { code: string; message: string } };

// Calls the Platform API through the dev proxy (/api) with the caller's
// access token. Throws Error with the envelope message on non-2xx.
export async function apiFetch(
  getAccessToken: () => Promise<string>,
  path: string,
  init?: RequestInit,
): Promise<Response> {
  const token = await getAccessToken();
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: { ...init?.headers, Authorization: `Bearer ${token}` },
  });
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
