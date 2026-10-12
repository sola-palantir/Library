import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  workers: 1,
  use: {
    baseURL: process.env.LIBRARY_BASE_URL || "http://127.0.0.1:5173",
    browserName: "chromium",
    launchOptions: { executablePath: process.env.CHROMIUM_PATH || undefined },
    viewport: { width: 1440, height: 1100 },
    screenshot: "only-on-failure",
  },
  reporter: "list",
});
