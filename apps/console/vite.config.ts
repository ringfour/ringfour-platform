import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { loadEnv } from "vite";
import { defineConfig } from "vitest/config";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: { "@": new URL("./src", import.meta.url).pathname },
    },
    server: {
      proxy: {
        "/api": {
          target: env.API_PROXY_TARGET || "http://localhost:8080",
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/api/, ""),
        },
        "/daemon": {
          target: env.DAEMON_PROXY_TARGET || "http://localhost:8081",
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/daemon/, ""),
        },
      },
    },
    test: {
      environment: "jsdom",
    },
  };
});
