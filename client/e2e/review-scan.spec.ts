import { test, expect } from "@playwright/test";

test("approved sample folder scans through native worker, cancels, retries and reviews real metadata", async ({
  page,
  request,
}, testInfo) => {
  test.skip(
    !process.env.LIBRARY_REVIEW_TEST_FOLDER,
    "Explicit local native read-only qualification only",
  );
  test.setTimeout(120000);
  const folder = process.env.LIBRARY_REVIEW_TEST_FOLDER!;
  const session = await (await request.get("/api/review-scan")).json();
  expect(session.enabled).toBe(true);
  expect(session.connected).toBe(true);
  expect(session.folder).toBe(folder);
  expect(session.encoding).toBe(false);
  expect(session.persistentPairing).toBe(false);
  expect(session.mediaTransfer).toBe(false);
  await page.goto("/");
  await page
    .getByRole("button", { name: "Add sample folder", exact: true })
    .click();
  await expect(page.getByLabel("Windows sample folder")).toHaveValue(folder);
  await page
    .getByRole("button", { name: "Add folder and scan read-only" })
    .click();
  await page.getByRole("button", { name: "Cancel scan", exact: true }).click();
  await expect(page.getByText("cancelled", { exact: true })).toBeVisible();
  let catalog = await (await request.get("/api/catalog?pageSize=100")).json();
  expect(catalog.total).toBe(0);
  await page
    .getByRole("button", { name: "Retry scan", exact: true })
    .first()
    .click();
  await expect(page.getByText("completed", { exact: true })).toBeVisible({
    timeout: 100000,
  });
  catalog = await (await request.get("/api/catalog?pageSize=100")).json();
  expect(catalog.total).toBe(2);
  for (const row of catalog.items) {
    expect(row.copy.state).toBe("observed");
    expect(row.copy.sha256).toBeNull();
  }
  const godzilla = catalog.items.find(
    (row: { copy: { relativePath: string } }) =>
      row.copy.relativePath.startsWith("Godzilla"),
  );
  const silo = catalog.items.find((row: { copy: { relativePath: string } }) =>
    row.copy.relativePath.startsWith("S01E01"),
  );
  expect(godzilla).toBeTruthy();
  expect(silo).toBeTruthy();
  await expect(
    page.getByLabel("Catalog file (current page)").locator("option"),
  ).toHaveCount(3);
  await page
    .getByLabel("Catalog file (current page)")
    .selectOption(String(godzilla.copy.id));
  await page
    .getByRole("button", { name: "Inspect read-only", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Technical inventory", exact: true }),
  ).toBeVisible();
  await expect(page.getByText(/Saved native scan observation/)).toBeVisible();
  await page
    .getByLabel("Original language override", { exact: false })
    .fill("jpn");
  const observation = await (
    await request.get(`/api/review-scan/copies/${godzilla.copy.id}`)
  ).json();
  expect(observation.evidence.executable).toBe(false);
  expect(observation.evidence.contentVerified).toBe(false);
  expect(
    observation.probe.streams.filter(
      (s: { codec_type: string; tags?: { language?: string } }) =>
        s.codec_type === "audio" && s.tags?.language === "jpn",
    ).length,
  ).toBeGreaterThan(0);
  expect(
    observation.probe.streams.filter(
      (s: {
        codec_type: string;
        codec_name: string;
        tags?: { language?: string };
      }) =>
        s.codec_type === "subtitle" &&
        s.codec_name === "hdmv_pgs_subtitle" &&
        s.tags?.language === "eng",
    ),
  ).toHaveLength(2);
  await page.screenshot({
    path: testInfo.outputPath("native-godzilla-review.png"),
    fullPage: true,
  });
  await page
    .getByLabel("Catalog file (current page)")
    .selectOption(String(silo.copy.id));
  await page
    .getByRole("button", { name: "Inspect read-only", exact: true })
    .click();
  await expect(
    page.locator("summary").filter({ hasText: /Video 0: hevc/ }),
  ).toBeVisible();
  const processing = await (await request.get("/api/media/processing")).json();
  expect(processing.sampleEncode).toBe(false);
  expect(processing.sampleRemux).toBe(false);
  expect(processing.hdrProcessing).toBe(false);
});
