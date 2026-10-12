import { test, expect } from "@playwright/test";

test("catalog scan, copy preview, verified copy, metadata and disc plan", async ({
  page,
  request,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto("/");
  await page.getByRole("button", { name: "Scan source", exact: true }).click();
  await expect(
    page.getByRole("button", { name: /Northlight/ }).first(),
  ).toBeVisible({ timeout: 15000 });
  await page
    .getByRole("button", { name: /Northlight/ })
    .first()
    .click();
  await expect(
    page.getByRole("heading", { name: "Northlight", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "../docs/screenshots/library.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Organize copy", exact: true })
    .click();
  await page
    .getByPlaceholder("Use Plex-compatible naming")
    .fill(`Movies/Northlight (2024)/Northlight-${Date.now()}.mkv`);
  await page.getByRole("button", { name: "Preview copy", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Your copy plan" }),
  ).toBeVisible();
  await page.screenshot({
    path: "../docs/screenshots/copy-preview.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Approve & copy" }).click();
  await page.getByRole("button", { name: /^Jobs/ }).click();
  await expect(
    page
      .locator(".job")
      .filter({ hasText: "copy" })
      .first()
      .getByText("completed", { exact: true }),
  ).toBeVisible({ timeout: 15000 });
  await page.getByRole("button", { name: "Library", exact: true }).click();
  await page
    .getByRole("button", { name: /Northlight/ })
    .first()
    .click();
  await page
    .getByRole("button", { name: "Edit metadata", exact: true })
    .click();
  await page.locator("#mode-genre").selectOption("set");
  await page.getByRole("textbox", { name: "genre value" }).fill("Adventure");
  await page.getByRole("button", { name: "Preview changes" }).click();
  await expect(
    page
      .locator(".metadata-preview")
      .getByText("Adventure", { exact: true })
      .last(),
  ).toBeVisible();
  await page.getByRole("button", { name: "Apply catalog changes" }).click();
  await expect(
    page.getByText("Catalog metadata updated. No media paths were changed."),
  ).toBeVisible();
  await page.getByRole("button", { name: "Discs", exact: true }).click();
  await expect(
    page
      .getByRole("checkbox", { name: "Select Northlight", exact: true })
      .first(),
  ).toBeVisible();
  const existingDiscCount = await page.locator(".disc-card").count();
  await page
    .locator("tbody tr")
    .filter({ has: page.locator(".badge.verified") })
    .first()
    .getByRole("checkbox")
    .check();
  await page.getByRole("button", { name: "Preview disc batches" }).click();
  await expect(
    page.getByRole("heading", { name: "Your disc batches" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Save disc plan" }).click();
  await expect(page.locator(".disc-card")).toHaveCount(existingDiscCount + 1);
  await page
    .getByRole("button", { name: "Stage files", exact: true })
    .first()
    .click();
  await page.getByRole("button", { name: /^Jobs/ }).click();
  await expect(
    page
      .locator(".job")
      .filter({ hasText: "disc stage" })
      .first()
      .getByText("completed", { exact: true }),
  ).toBeVisible({ timeout: 15000 });
  const exportResponse = await request.get("http://127.0.0.1:5080/api/export");
  expect(exportResponse.ok()).toBeTruthy();
  const exported = await exportResponse.json();
  expect(exported.schemaVersion).toBe(3);
  expect(exported.history.length).toBeGreaterThan(0);
  expect(errors).toEqual([]);
});

test("keyboard search, empty results, mobile navigation and reduced motion", async ({
  page,
}) => {
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "The library" }),
  ).toBeVisible();
  await page.getByRole("heading", { name: "The library" }).click();
  await page.keyboard.press("/");
  await expect(
    page.getByRole("textbox", { name: "Search media" }),
  ).toBeFocused();
  await page.keyboard.type("no-title-matches-this");
  await expect(
    page.getByRole("heading", { name: "No matching media" }),
  ).toBeVisible();
  await page.getByRole("textbox", { name: "Search media" }).fill("");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Workspace settings" }),
  ).toBeVisible();
  await page.screenshot({
    path: "../docs/screenshots/mobile-settings.png",
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBeTruthy();
});

test("API rejects cross-origin mutations, validates XML, and exports a SQLite backup", async ({
  request,
}) => {
  const base = "http://127.0.0.1:5080/api";
  const blocked = await request.post(base + "/scan", {
    headers: { Origin: "https://untrusted.example" },
    data: { rootId: "source" },
  });
  expect(blocked.status()).toBe(403);
  const wrongType = await request.post(base + "/scan", {
    headers: { "Content-Type": "text/plain" },
    data: '{"rootId":"source"}',
  });
  expect(wrongType.status()).toBe(415);
  const catalog = await (await request.get(base + "/catalog")).json();
  const titleId = catalog.items[0].title.id;
  const xxe = await request.post(base + "/nfo/import-preview", {
    data: {
      titleId,
      xml: '<!DOCTYPE movie [<!ENTITY x SYSTEM "file:///etc/passwd">]><movie><title>&x;</title></movie>',
    },
  });
  expect(xxe.status()).toBe(409);
  const backup = await request.get(base + "/backup");
  expect(backup.ok()).toBeTruthy();
  expect((await backup.body()).subarray(0, 16).toString()).toBe(
    "SQLite format 3\0",
  );
});

test("synthetic torrent completion and reviewable metadata matching remain explicit", async ({
  page,
  request,
}) => {
  const base = "http://127.0.0.1:5080/api";
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByLabel("qBittorrent mode").selectOption("synthetic");
  await page
    .getByRole("button", { name: "Save qBittorrent configuration" })
    .click();
  await expect(
    page.getByText("Configuration saved. No connection was made."),
  ).toBeVisible();
  try {
    await page.getByRole("button", { name: "Refresh torrent status" }).click();
    await expect(
      page
        .locator(".integration-panel")
        .getByText("complete · 100%", { exact: true }),
    ).toBeVisible();
    await expect(
      page
        .locator(".integration-panel")
        .getByText("incomplete · 42%", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText(/No qBittorrent server was contacted/),
    ).toBeVisible();
    await page.screenshot({
      path: "../docs/screenshots/integrations.png",
      fullPage: true,
    });
    const catalog = await (
      await request.get(base + "/catalog?role=source")
    ).json();
    const incomplete = catalog.items.find(
      (r: any) => r.title.name === "Paper Moons",
    );
    const blocked = await request.post(base + "/copy/preview", {
      data: {
        sourceCopyId: incomplete.copy.id,
        targetRootId: "library",
        targetPath: `Movies/Blocked-${Date.now()}.mkv`,
      },
    });
    expect(blocked.status()).toBe(409);
    expect((await blocked.json()).error).toContain("not ready");
    const complete = catalog.items.find(
      (r: any) => r.title.name === "Northlight",
    );
    const accepted = await request.post(base + "/copy/preview", {
      data: {
        sourceCopyId: complete.copy.id,
        targetRootId: "library",
        targetPath: `Movies/Ready-${Date.now()}.mkv`,
      },
    });
    expect(accepted.ok()).toBeTruthy();
    const proposal = await accepted.json();
    expect(proposal.completion.synthetic).toBe(true);
    expect(proposal.completion.state).toBe("complete");
    await page.getByRole("button", { name: "Library", exact: true }).click();
    await page
      .getByRole("button", { name: /Northlight/ })
      .first()
      .click();
    await page.getByText("Find a metadata match", { exact: true }).click();
    await page
      .getByLabel("Match title", { exact: true })
      .fill("No such synthetic title");
    await page
      .getByRole("button", { name: "Search synthetic metadata" })
      .click();
    await expect(page.getByText(/No synthetic matches/)).toBeVisible();
    await page.getByLabel("Match title", { exact: true }).fill("Northlight");
    await page
      .getByRole("button", { name: "Search synthetic metadata" })
      .click();
    await page.getByRole("button", { name: "Review match · 2024" }).click();
    await expect(
      page.locator("dialog").getByText(/SYNTHETIC MATCH/),
    ).toBeVisible();
    const before = await (
      await request.get(base + "/catalog?role=source&q=Northlight")
    ).json();
    expect(before.items[0].title.revision).toBe(complete.title.revision);
    await page.screenshot({
      path: "../docs/screenshots/match-preview.png",
      fullPage: true,
    });
    await page.getByRole("button", { name: "Apply catalog changes" }).click();
    await expect(
      page.getByText("Catalog metadata updated. No media paths were changed."),
    ).toBeVisible();
    const after = await (
      await request.get(base + "/catalog?role=source&q=Northlight")
    ).json();
    expect(after.items[0].title.revision).toBe(complete.title.revision + 1);
  } finally {
    const state = await (await request.get(base + "/integrations/qbit")).json();
    const reset = await request.put(base + "/integrations/qbit", {
      data: { ...state.configuration, mode: "disabled" },
    });
    expect(reset.ok()).toBeTruthy();
  }
});
