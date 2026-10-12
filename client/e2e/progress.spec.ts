import { test, expect } from "@playwright/test";

test("measured progress, repeated actions, recovery, errors and reduced motion", async ({
  page,
}) => {
  test.setTimeout(60000);
  const now = new Date().toISOString();
  let status = "running",
    phase = "copying",
    done = 25,
    total: number | null = 100;
  let cancelRequested = false,
    unavailable = false,
    failRetry = false;
  let cancellations = 0,
    retries = 0;
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/jobs" && unavailable) return route.abort();
    let result: unknown = {};
    if (path === "/api/catalog") result = { total: 0, items: [] };
    else if (path === "/api/settings") result = { roots: [], preferences: {} };
    else if (path === "/api/discs") result = [];
    else if (path === "/api/summary")
      result = {
        titles: 0,
        copies: 0,
        libraryBytes: 0,
        sourceCopies: 0,
        discs: 0,
        activeJobs: 1,
      };
    else if (path === "/api/jobs/synthetic-progress/cancel") {
      cancellations++;
      cancelRequested = true;
      await new Promise((resolve) => setTimeout(resolve, 200));
    } else if (path === "/api/jobs/synthetic-progress/retry") {
      retries++;
      if (failRetry)
        return route.fulfill({
          status: 409,
          json: { error: "Synthetic retry refused" },
        });
      status = "queued";
      cancelRequested = false;
    } else if (path === "/api/jobs")
      result = [
        {
          id: "synthetic-progress",
          type: phase === "scanning" ? "scan" : "copy",
          status,
          payload: "{}",
          progressBytes: 25,
          cancelRequested,
          createdUtc: now,
          error:
            status === "failed"
              ? "Synthetic destination unavailable; source retained."
              : null,
          progressJson: JSON.stringify({
            phase,
            bytes: done,
            totalBytes: total,
            files: phase === "scanning" ? 37 : null,
            atUtc: now,
          }),
        },
      ];
    await route.fulfill({ json: result });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Jobs", exact: true }).click();
  let bar = page.getByRole("progressbar");
  await expect(bar).toHaveAttribute("aria-valuenow", "25");
  phase = "verifying-staged-copy";
  done = 50;
  await expect(bar).toHaveAccessibleName("Verifying temporary copy · SHA-256", {
    timeout: 6000,
  });
  await expect(bar).toHaveAttribute("aria-valuenow", "50");
  await page.screenshot({
    path: "../docs/screenshots/job-progress.png",
    fullPage: true,
  });
  phase = "scanning";
  total = null;
  await expect(bar).not.toHaveAttribute("aria-valuenow", /.*/, {
    timeout: 6000,
  });
  await expect(page.getByText(/37 files examined/)).toBeVisible();
  await expect(page.locator(".indeterminate span")).toHaveCSS(
    "animation-name",
    "progress-travel",
  );
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect(page.locator(".indeterminate span")).toHaveCSS(
    "animation-name",
    "none",
  );
  await expect(page.locator(".progress-track span")).toHaveCSS(
    "transition-duration",
    "0s",
  );
  await page.emulateMedia({ reducedMotion: "no-preference" });
  unavailable = true;
  await expect(page.getByRole("alert")).toContainText(
    "Job updates unavailable",
    { timeout: 6000 },
  );
  await expect(page.locator(".indeterminate span")).toHaveCSS(
    "animation-name",
    "none",
  );
  unavailable = false;
  await expect(page.getByRole("alert")).toHaveCount(0, { timeout: 6000 });
  const cancel = page.getByRole("button", { name: "Cancel", exact: true });
  await cancel.focus();
  await page.keyboard.press("Enter");
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("button", { name: "Cancellation requested" }),
  ).toBeDisabled();
  expect(cancellations).toBe(1);
  status = "cancelled";
  await expect(
    page.getByRole("button", { name: "Retry", exact: true }),
  ).toBeVisible({ timeout: 6000 });
  await expect(bar).toHaveCount(0);
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByText(/Waiting for worker/)).toBeVisible();
  await expect(bar).toHaveCount(0);
  status = "running";
  phase = "checking-recovery";
  total = 100;
  done = 0;
  await expect(page.getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "0",
    { timeout: 6000 },
  );
  status = "failed";
  await expect(
    page.getByText("Synthetic destination unavailable; source retained."),
  ).toBeVisible({ timeout: 6000 });
  failRetry = true;
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Synthetic retry refused",
  );
  expect(retries).toBe(2);
  status = "completed";
  phase = "verifying-published-copy";
  done = 100;
  await expect(page.locator(".job-progress")).toHaveClass(/is-complete/, {
    timeout: 6000,
  });
  await expect(page.getByRole("progressbar")).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test("catalog loading skeleton is temporary and has no infinite shimmer", async ({
  page,
}) => {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/api/catalog?**", async (route) => {
    await gate;
    await route.fulfill({ json: { total: 0, items: [] } });
  });
  await page.goto("/", { waitUntil: "domcontentloaded" });
  await expect(
    page.getByRole("status", { name: "Loading catalog" }),
  ).toBeVisible();
  await expect(page.locator(".skeleton-row").first()).toHaveCSS(
    "animation-iteration-count",
    "1",
  );
  release();
  await expect(
    page.getByRole("status", { name: "Loading catalog" }),
  ).toHaveCount(0);
  await expect(page.getByText("A home for every copy")).toBeVisible();
});
