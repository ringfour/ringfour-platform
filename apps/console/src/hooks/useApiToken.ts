import { useAuth0 } from "@auth0/auth0-react";
import { useCallback } from "react";
import type { Token } from "@/lib/apps";

/** Stable token provider; the API layer owns expiry/redirect handling. */
export function useApiToken(): Token {
  const { getAccessTokenSilently } = useAuth0();
  return useCallback(async () => {
    const token = await getAccessTokenSilently({
      authorizationParams: { audience: import.meta.env.VITE_AUTH0_AUDIENCE },
    });
    if (!token) throw new Error("Sign in required");
    return token;
  }, [getAccessTokenSilently]);
}
