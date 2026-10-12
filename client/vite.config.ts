import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": {
        target: "http://127.0.0.1:5080",
        changeOrigin: true,
        configure(proxy) {
          proxy.on("proxyReq", (proxyReq, request) => {
            // Development proxy only. Preserve the service's strict same-origin protection.
            if (request.headers.origin === "http://127.0.0.1:5173")
              proxyReq.setHeader("Origin", "http://127.0.0.1:5080");
          });
        },
      },
    },
  },
});
