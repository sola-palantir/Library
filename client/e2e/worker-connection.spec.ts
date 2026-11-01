import { expect, test } from "@playwright/test";

test("unconfigured connection explains approval without creating access", async ({
  page,
}) => {
  test.skip(
    !!process.env.LIBRARY_CONNECTION_TEST,
    "Default unconfigured API only",
  );
  await page.goto("/");
  await page.getByRole("button", { name: "Media review", exact: true }).click();
  const section = page.getByRole("region", {
    name: "Windows worker connection",
  });
  await expect(
    section.getByText("Local authorization required", { exact: true }),
  ).toBeVisible();
  const mutations: string[] = [];
  page.on("request", (request) => {
    if (request.method() !== "GET" && request.url().includes("/api/"))
      mutations.push(request.url());
  });
  await section
    .getByRole("button", { name: "Connect Windows worker", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toContainText(
    "cannot create credentials or renew expired access",
  );
  await expect(
    page.getByRole("button", { name: "Connect read-only", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Close", exact: true }).click();
  expect(mutations).toEqual([]);
});

test("configured synthetic API connects, disconnects, cancels, reconnects and revokes", async ({
  page,
  request,
}, testInfo) => {
  test.skip(
    !process.env.LIBRARY_CONNECTION_TEST,
    "Isolated short-lived synthetic connection API only",
  );
  test.setTimeout(120000);
  const state = await (await request.get("/api/review-scan")).json();
  expect(state.connection.configured).toBe(true);
  expect(state.connection.state).toBe("disconnected");
  expect(state.encoding).toBe(false);
  const authorizationExpiry = state.connection.accessExpiresUtc;
  expect((await request.get("/api/review-connection/worker")).status()).toBe(
    401,
  );
  expect(
    (
      await request.get("/api/review-connection/worker", {
        headers: { Authorization: `Bearer ${"b".repeat(64)}` },
      })
    ).status(),
  ).toBe(401);
  const syntheticHeaders = { Authorization: `Bearer ${"a".repeat(64)}` };
  await page.goto("/");
  await page.getByRole("button", { name: "Media review", exact: true }).click();
  const section = page.getByRole("region", {
    name: "Windows worker connection",
  });
  async function connect() {
    await section
      .getByRole("button", { name: "Connect Windows worker", exact: true })
      .click();
    await expect(page.getByRole("dialog")).toContainText(state.folder);
    await page
      .getByLabel("Connect within this approved folder and expiry")
      .check();
    await page
      .getByRole("button", { name: "Connect read-only", exact: true })
      .click();
    await expect(
      section.getByText("Connected for read-only scanning", { exact: true }),
    ).toBeVisible({ timeout: 10000 });
  }
  await connect();
  const firstLease = await (
    await request.get("/api/review-connection/worker", {
      headers: syntheticHeaders,
    })
  ).json();
  expect(
    (
      await request.get("/api/review-worker/command", {
        headers: {
          ...syntheticHeaders,
          "X-Library-Review-Session": "0".repeat(32),
        },
      })
    ).status(),
  ).toBe(401);
  expect(
    (
      await request.post("/api/review-scan", {
        data: { folder: "C:\\not-approved" },
      })
    ).status(),
  ).toBe(409);
  await page
    .getByRole("button", { name: "Add folder and scan read-only" })
    .click();
  await expect(page.getByText("completed", { exact: true })).toBeVisible({
    timeout: 20000,
  });
  const catalog = await (await request.get("/api/catalog?pageSize=100")).json();
  expect(catalog.total).toBe(1);
  expect(catalog.items[0].copy.sha256).toBeNull();
  const first = await (await request.get("/api/review-scan")).json();
  // Queued or running cancellation must preserve the completed observation.
  await page
    .getByRole("button", { name: "Add folder and scan read-only" })
    .click();
  await section
    .getByRole("button", { name: "Disconnect and cancel scans" })
    .click();
  await expect(
    section.getByText("Disconnected", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("cancelled", { exact: true })).toBeVisible();
  await connect();
  const nextLease = await (
    await request.get("/api/review-connection/worker", {
      headers: syntheticHeaders,
    })
  ).json();
  expect(nextLease.sessionId).not.toBe(firstLease.sessionId);
  expect(
    (
      await request.get("/api/review-worker/command", {
        headers: {
          ...syntheticHeaders,
          "X-Library-Review-Session": firstLease.sessionId,
        },
      })
    ).status(),
  ).toBe(401);
  const again = await (await request.get("/api/review-scan")).json();
  expect(again.connection.accessExpiresUtc).toBe(authorizationExpiry);
  expect(again.expiresUtc).not.toBeNull();
  expect(first.encoding).toBe(false);
  await section
    .getByRole("button", { name: "Forget access", exact: true })
    .click();
  await page
    .getByLabel("Revoke this read-only authorization", { exact: true })
    .check();
  await page
    .getByRole("button", { name: "Revoke access now", exact: true })
    .click();
  await expect(
    section.getByText("Access revoked", { exact: true }),
  ).toBeVisible();
  expect(
    (
      await request.post("/api/review-connection/connect", {
        data: { folder: state.folder },
      })
    ).status(),
  ).toBe(409);
  const saved = await request.get(
    `/api/review-scan/copies/${catalog.items[0].copy.id}`,
  );
  expect(saved.status()).toBe(200);
  expect((await saved.json()).evidence.cached).toBe(true);
  await page
    .getByLabel("Catalog file (current page)")
    .selectOption(String(catalog.items[0].copy.id));
  await page
    .getByRole("button", { name: "Inspect read-only", exact: true })
    .click();
  await expect(page.getByText(/Saved native scan observation/)).toBeVisible();
  await expect(
    page.locator("summary").filter({ hasText: /Video 0: ffv1/ }),
  ).toBeVisible();
  await page.screenshot({
    path: testInfo.outputPath("revoked-saved-review.png"),
    fullPage: true,
  });
  expect(
    (await (await request.get("/api/catalog?pageSize=100")).json()).total,
  ).toBe(1);
});
