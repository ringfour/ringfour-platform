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
