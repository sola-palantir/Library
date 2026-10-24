import { test, expect } from "@playwright/test";
import { syntheticProbe } from "../src/mediaFixture";

test("media review distinguishes synthetic evidence and resolves tracks without media mutations", async ({
  page,
}) => {
  const mutations: string[] = [];
  page.on("request", (request) => {
    if (request.url().includes("/api/") && request.method() !== "GET")
      mutations.push(request.url());
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Media review", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Know your media" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Load synthetic example" }).click();
  await expect(
    page.getByText(
      "Synthetic demonstration — invented metadata, not a real file.",
    ),
  ).toBeVisible();
  await page
    .getByLabel("Original language override", { exact: false })
    .fill("jpn");
  await expect(
    page.getByRole("row").filter({ hasText: "Director commentary" }),
  ).toContainText("exclude");
  await expect(
    page.getByRole("row").filter({ hasText: "Audio description" }),
  ).toContainText("keep");
  await expect(
    page.getByRole("status").filter({ hasText: "Review required" }),
  ).toBeVisible();
  await page.getByLabel("Language for stream 8", { exact: true }).fill("eng");
  await page
    .getByRole("combobox", { name: "Role for stream 8", exact: true })
    .selectOption("main");
  await expect(
    page.getByText("Selection preview ready — no processing approved"),
  ).toBeVisible();
  await page
    .getByLabel("Preview HandBrake preset", { exact: true })
    .setInputFiles({
      name: "synthetic-preset.json",
      mimeType: "application/json",
      buffer: Buffer.from(
        JSON.stringify({
          PresetList: [
            {
              PresetName: "Synthetic RF22",
              FileFormat: "av_mkv",
              VideoEncoder: "x265_10bit",
              VideoQualityType: 2,
              VideoQualitySlider: 22,
              VideoPreset: "slow",
              AudioList: [{ AudioEncoder: "av_aac" }],
              AudioEncoderFallback: "av_aac",
            },
          ],
        }),
      ),
    });
  await expect(
    page.getByRole("heading", { name: "Synthetic RF22" }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Preset creates encoded audio. Library policy requires copy-only retained audio.",
    ),
  ).toBeVisible();
  expect(mutations).toEqual([]);
});

test("local probe import preserves technical metadata and rejects invalid inventories", async ({
  page,
}) => {
  const mutations: string[] = [];
  page.on("request", (request) => {
    if (request.url().includes("/api/") && request.method() !== "GET")
      mutations.push(request.url());
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Media review", exact: true }).click();
  const upload = page.getByLabel("Import FFprobe JSON", { exact: true });
  await upload.setInputFiles({
    name: "invented-probe.json",
    mimeType: "application/json",
    buffer: Buffer.from(JSON.stringify(syntheticProbe)),
  });
  await expect(
    page.getByText(
      "Imported metadata: invented-probe.json. Unverified; not attached to a catalog file.",
    ),
  ).toBeVisible();
  await page.locator("summary").filter({ hasText: "Video 0:" }).click();
  await expect(
    page.getByText("Average / nominal frame rate", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("24000/1001 / 24000/1001 (source ratios)", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("smpte2084 / bt2020 / bt2020nc", { exact: true }),
  ).toBeVisible();
  await upload.setInputFiles({
    name: "invalid-probe.json",
    mimeType: "application/json",
    buffer: Buffer.from('{"streams":[]}'),
  });
  await expect(
    page
      .getByRole("alert")
      .filter({ hasText: /Probe must contain 1.256 streams\./ }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Imported metadata: invented-probe.json. Unverified; not attached to a catalog file.",
    ),
  ).toBeVisible();
  await page.setViewportSize({ width: 430, height: 932 });
  await expect(
    page.getByRole("heading", { name: "Technical inventory" }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(mutations).toEqual([]);
});
