import { test, expect } from "@playwright/test";
import { syntheticProbe } from "../src/mediaFixture";

for (const mode of ["stream-copy", "x265-sdr-sample"]) {
  test(`${mode} sample requires live inspection, separate approval and explicit withdrawal`, async ({
    page,
  }) => {
    const probe = structuredClone(syntheticProbe);
    Object.assign(probe.streams[0], {
      codec_name: "h264",
      pix_fmt: "yuv420p",
      color_transfer: "bt709",
      color_primaries: "bt709",
      color_space: "bt709",
      side_data_list: [],
    });
    let captured: any = null;
    let approvals = 0;
    let status = "draft";
    const job = () => ({
      id: "synthetic-sample-plan",
      type: "sample-remux",
      status,
      payload: "{}",
      error: null,
      progressBytes: 0,
      cancelRequested: false,
      createdUtc: "2026-01-01",
      updatedUtc: "2026-01-01",
    });
    await page.route("**/api/**", async (route) => {
      const path = new URL(route.request().url()).pathname;
      let value: unknown = {};
      if (path === "/api/catalog")
        value = {
          total: 1,
          items: [
            {
              title: {
                id: 1,
                name: "Invented source",
                kind: "movie",
                year: 2026,
                lockedFields: "[]",
                revision: 0,
              },
              edition: { id: 1, name: "Original" },
              asset: { id: 1, fileName: "invented.mkv", bytes: 100 },
              copy: {
                id: 7,
                assetId: 1,
                rootId: "source",
                relativePath: "invented.mkv",
                role: "source",
                state: "online",
                bytes: 100,
                firstSeenUtc: "2026-01-01",
              },
              reviewEligible: false,
            },
          ],
        };
      else if (path === "/api/settings")
        value = {
          preferences: {
            seedRetentionDays: 20,
            discCapacityBytes: 50000000000,
            discReserveBytes: 1000000000,
          },
          roots: [
            {
              id: "staging",
              label: "Invented staging",
              role: "staging",
              online: true,
              containerPath: "/invented",
              windowsPath: "",
            },
          ],
        };
      else if (path === "/api/jobs") value = status === "draft" ? [] : [job()];
      else if (path === "/api/discs") value = [];
      else if (path === "/api/summary")
        value = {
          titles: 1,
          copies: 1,
          libraryBytes: 0,
          sourceCopies: 1,
          discs: 0,
          activeJobs: 0,
        };
      else if (path === "/api/media/inspect/7")
        value = {
          probe,
          evidence: {
            copyId: 7,
            observedUtc: "2026-01-01",
            toolVersion: "synthetic browser contract",
          },
        };
      else if (path === "/api/media/remux/preview") {
        captured = route.request().postDataJSON();
        value = {
          job: job(),
          targetPath: "samples/synthetic-sample-plan.mkv",
          executionAvailable: true,
          warning:
            "Synthetic sample plan; no real media is processed in this browser contract.",
          plan: {
            encoding:
              captured.mode === "x265-sdr-sample"
                ? {
                    id: "handbrake-1080-rf22-slow-v1",
                    presetSha256:
                      "2b53699a614ea5b260d844a37e4f79425c8ae40b2abca21f677dc63f33e2b383",
                    crf: 22,
                    preset: "slow",
                    pixelFormat: "yuv420p",
                  }
                : null,
            streams: [
              { index: 1, type: "audio", codec: "truehd", language: "jpn" },
            ],
          },
        };
      } else if (path.endsWith("/approve")) {
        approvals++;
        status = "completed";
      } else if (path.endsWith("/withdraw")) status = "withdrawn";
      await route.fulfill({ json: value });
    });
    await page.goto("/");
    await page
      .getByRole("button", { name: "Media review", exact: true })
      .click();
    await page
      .getByRole("combobox", {
        name: "Catalog file (current page)",
        exact: true,
      })
      .selectOption("7");
    await page.getByRole("button", { name: "Inspect read-only" }).click();
    await page
      .getByLabel("Original language override", { exact: false })
      .fill("jpn");
    await page.getByLabel("Language for stream 8", { exact: true }).fill("eng");
    await page
      .getByRole("combobox", { name: "Role for stream 8", exact: true })
      .selectOption("main");
    await page
      .getByRole("combobox", {
        name: "Sample staging destination",
        exact: true,
      })
      .selectOption("staging");
    await expect(
      page.getByRole("button", { name: "Preview sample plan" }),
    ).toBeDisabled();
    await page
      .getByLabel("I confirm this source is complete", { exact: false })
      .check();
    await page.getByLabel("I reviewed this as SDR", { exact: false }).check();
    await page
      .getByRole("combobox", { name: "Sample mode", exact: true })
      .selectOption(mode);
    await page.getByRole("button", { name: "Preview sample plan" }).click();
    await expect(
      page.getByRole("heading", { name: "Review the sample plan" }),
    ).toBeVisible();
    expect(captured.originalLanguages).toEqual(["jpn"]);
    expect(captured.mode).toBe(mode);
    if (mode === "x265-sdr-sample")
      await expect(
        page.getByText("This is a calibration sample", { exact: false }),
      ).toBeVisible();
    expect(captured.tracks.find((t: any) => t.index === 3).role).toBe(
      "commentary",
    );
    expect(approvals).toBe(0);
    await page.getByRole("button", { name: "Create validated sample" }).click();
    await expect(
      page.getByRole("status").filter({ hasText: "Sample job: completed" }),
    ).toBeVisible();
    expect(approvals).toBe(1);
    await page.screenshot({
      path: `../docs/screenshots/sample-${mode}.png`,
      fullPage: true,
    });
    await page.getByRole("button", { name: "Withdraw sample" }).click();
    await expect(
      page.getByRole("status").filter({ hasText: "Sample job: withdrawn" }),
    ).toBeVisible();
    await page.setViewportSize({ width: 430, height: 932 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
    await page.screenshot({
      path: `../docs/screenshots/sample-${mode}-mobile.png`,
      fullPage: true,
    });
  });
}
