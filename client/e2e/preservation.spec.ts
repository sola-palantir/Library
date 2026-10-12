import { test, expect } from "@playwright/test";

test("real backup API stages a partial season and preserves idempotent history", async ({
  page,
  request,
}) => {
  const waitJob = async (id: string) => {
    await expect
      .poll(
        async () => {
          const jobs = await (await request.get("/api/jobs")).json();
          return jobs.find((j: { id: string }) => j.id === id)?.status;
        },
        { timeout: 15000 },
      )
      .toBe("completed");
  };
  const scan = await (
    await request.post("/api/scan", { data: { rootId: "source" } })
  ).json();
  await waitJob(scan.id);
  const catalog = await (
    await request.get("/api/catalog?role=source&q=Signal%20Coast")
  ).json();
  expect(catalog.items.length).toBe(2);
  const stamp = Date.now();
  for (const row of catalog.items) {
    const preview = await (
      await request.post("/api/copy/preview", {
        data: {
          sourceCopyId: row.copy.id,
          targetRootId: "library",
          targetPath: `TV/Preservation-${stamp}/S01E0${row.edition.episode}.mkv`,
        },
      })
    ).json();
    expect(preview.job).toBeTruthy();
    expect(
      (
        await request.post(`/api/copy/${preview.job.id}/commit`, { data: {} })
      ).ok(),
    ).toBe(true);
    await waitJob(preview.job.id);
  }
  await page.goto("/");
  await page.getByRole("button", { name: "Discs", exact: true }).click();
  const searchResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      url.pathname === "/api/catalog" &&
      url.searchParams.get("q") === `Preservation-${stamp}`
    );
  });
  await page
    .getByRole("textbox", { name: "Search media" })
    .fill(`Preservation-${stamp}`);
  await searchResponse;
  await expect(
    page.getByRole("checkbox", { name: "Select Signal Coast", exact: true }),
  ).toHaveCount(2);
  for (const checkbox of await page
    .getByRole("checkbox", { name: "Select Signal Coast", exact: true })
    .all())
    await checkbox.check();
  await page
    .getByLabel("Backup name", { exact: true })
    .fill(`Signal Coast — synthetic season ${stamp}`);
  await page.getByLabel("Declare a TV season scope").check();
  await page.getByLabel("Series title", { exact: true }).fill("Signal Coast");
  await page.getByLabel("Expected episodes", { exact: true }).fill("1-3");
  await page.getByLabel("Planning capacity · bytes").fill("800000");
  await page.getByLabel("Reserved space · bytes").fill("10000");
  const previewResponse = page.waitForResponse(
    (r) =>
      r.url().endsWith("/api/backups/preview") &&
      r.request().method() === "POST",
  );
  await page
    .getByRole("button", { name: "Preview whole-file packing" })
    .click();
  const proposal = await (await previewResponse).json();
  expect(proposal.preview.coverage.missing).toEqual([3]);
  expect(proposal.preview.packing.discs.length).toBe(2);
  await expect(
    page.getByText("Missing: 3", { exact: true }).first(),
  ).toBeVisible();
  await page.getByLabel("Search backup history").fill(String(stamp));
  await page.screenshot({
    path: "../docs/screenshots/backup-plan.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Record this backup plan" }).click();
  await expect(page.getByRole("status")).toContainText("Backup recorded");
  const history = await (await request.get(`/api/backups?q=${stamp}`)).json();
  expect(history.discs.length).toBe(2);
  expect(history.coverage[0].entireSeasonVerified).toBe(false);
  expect(history.coverage[0].scope.missing).toEqual([3]);
  // Retrying the same approved preview with a different client command cannot create another set.
  const duplicate = await request.post(
    `/api/backups/${proposal.job.id}/approve`,
    { data: { commandId: "synthetic-lost-response-retry" } },
  );
  expect(duplicate.ok()).toBe(true);
  expect((await duplicate.json()).id).toBe(history.sets[0].id);
  for (const disc of history.discs) {
    const staged = await request.post(`/api/backups/discs/${disc.id}/stage`, {
      data: { rootId: "staging" },
    });
    expect(staged.ok()).toBe(true);
    await waitJob((await staged.json()).id);
    const manifest = await (
      await request.get(`/api/backups/discs/${disc.id}/manifest`)
    ).json();
    expect(manifest.discId).toBe(disc.id);
    expect(manifest.files.length).toBe(1);
  }
  const forged = await request.post("/api/backups/receipts", {
    data: { payload: "{}", signature: "" },
  });
  expect(forged.status()).toBe(409);
  await page
    .getByRole("button", { name: "Refresh history", exact: true })
    .click();
  await page.screenshot({
    path: "../docs/screenshots/backup-history.png",
    fullPage: true,
  });
  const after = await (await request.get(`/api/backups?q=${stamp}`)).json();
  expect(
    after.evidence.filter((e: { state: string }) => e.state === "staged"),
  ).toHaveLength(2);
  expect(after.coverage[0].selectedScopeVerified).toBe(false);
});
