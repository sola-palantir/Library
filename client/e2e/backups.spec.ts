import { test, expect } from "@playwright/test";

test("backup workspace keeps season gaps explicit and records only reviewed plans", async ({
  page,
}) => {
  let approvals = 0;
  const approvalCommands: string[] = [];
  let lastPreview: Record<string, unknown> = {};
  await page.route("**/api/**", async (route) => {
    const url = new URL(route.request().url());
    let result: unknown = {};
    if (url.pathname === "/api/catalog")
      result = {
        total: 1,
        items: [
          {
            title: {
              id: 1,
              name: "Signal Coast",
              kind: "tv",
              year: 2023,
              lockedFields: "[]",
              revision: 1,
            },
            edition: { id: 1, name: "Original", season: 1, episode: 1 },
            asset: {
              id: 1,
              fileName: "Signal.Coast.S01E01.mkv",
              bytes: 3_000_000_000,
              sha256: "a".repeat(64),
            },
            copy: {
              id: 1,
              assetId: 1,
              rootId: "library",
              relativePath: "Signal Coast/S01E01.mkv",
              role: "library",
              state: "verified",
              bytes: 3_000_000_000,
              modifiedTicks: 1,
              firstSeenUtc: "2026-01-01",
              sha256: "a".repeat(64),
            },
            reviewEligible: false,
          },
        ],
      };
    else if (url.pathname === "/api/settings")
      result = {
        preferences: {
          seedRetentionDays: 20,
          discCapacityBytes: 50_000_000_000,
          discReserveBytes: 1_000_000_000,
        },
        roots: [
          {
            id: "stage",
            role: "staging",
            label: "Synthetic staging",
            online: true,
            containerPath: "/synthetic/stage",
            windowsPath: "",
          },
        ],
      };
    else if (["/api/jobs", "/api/discs"].includes(url.pathname)) result = [];
    else if (url.pathname === "/api/summary")
      result = {
        titles: 1,
        copies: 1,
        libraryBytes: 3_000_000_000,
        sourceCopies: 0,
        discs: 0,
        activeJobs: 0,
      };
    else if (url.pathname === "/api/backups")
      result = { sets: [], discs: [], evidence: [], coverage: [] };
    else if (url.pathname === "/api/backups/preview") {
      lastPreview = route.request().postDataJSON();
      result = {
        job: { id: "synthetic-preview" },
        preview: {
          sha256: "b".repeat(64),
          request: lastPreview,
          packing: {
            discs: [
              [
                {
                  id: 1,
                  title: "Signal Coast",
                  episode: 1,
                  files: [
                    {
                      path: "Signal Coast/S01E01.mkv",
                      bytes: 3_000_000_000,
                      sha256: "a".repeat(64),
                      sidecar: false,
                    },
                  ],
                },
              ],
            ],
            unselectedBundleIds: [],
            optimalityProven: true,
            method: "Exact synthetic test",
            nodes: 2,
          },
          coverage: {
            expected: [1, 2, 3],
            selected: [1],
            missing: [2],
            excluded: [3],
            entireSeasonIncluded: false,
          },
        },
      };
    } else if (url.pathname === "/api/backups/synthetic-preview/approve") {
      approvals++;
      approvalCommands.push(route.request().postDataJSON().commandId);
      if (approvals === 1) {
        await route.fulfill({
          status: 503,
          json: { error: "Synthetic lost completion response; retry safely." },
        });
        return;
      }
      result = { id: "synthetic-set" };
    }
    await route.fulfill({ json: result });
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Discs", exact: true }).click();
  await page
    .getByRole("checkbox", { name: "Select Signal Coast", exact: true })
    .check();
  await expect(
    page.getByRole("heading", { name: "Plan a backup that can be proved" }),
  ).toBeVisible();
  await page.getByLabel("Declare a TV season scope").check();
  await page.getByLabel("Series title", { exact: true }).fill("Signal Coast");
  await page.getByLabel("Expected episodes", { exact: true }).fill("1-3");
  await page.getByLabel("Explicit exclusions", { exact: true }).fill("3");
  await page
    .getByRole("button", { name: "Preview whole-file packing" })
    .click();
  await expect(page.getByText("Missing: 2", { exact: true })).toBeVisible();
  await expect(
    page.getByText("Explicitly excluded: 3", { exact: true }),
  ).toBeVisible();
  expect(lastPreview.season).toEqual({
    title: "Signal Coast",
    season: 1,
    expectedEpisodes: [1, 2, 3],
    excludedEpisodes: [3],
  });
  expect(approvals).toBe(0);
  await page.getByRole("button", { name: "Record this backup plan" }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Synthetic lost completion response",
  );
  await page.getByRole("button", { name: "Record this backup plan" }).click();
  await expect(page.getByRole("status")).toContainText("Backup recorded");
  expect(approvals).toBe(2);
  expect(approvalCommands[0]).toBe(approvalCommands[1]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
