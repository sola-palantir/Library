import { test, expect } from "@playwright/test";

test("Windows control plane stays useful without media mounts or a worker", async ({
  page,
  request,
}, testInfo) => {
  const execution = await (await request.get("/api/execution")).json();
  test.skip(
    execution.backend !== "windows-worker",
    "Dedicated Windows control-plane run only",
  );
  expect(execution.workerConnected).toBe(false);
  expect(execution.localExecution).toBe(false);
  expect(execution.mediaTransfer).toBe(false);
  await page.goto("/");
  await expect(page.getByRole("status")).toContainText("Media jobs are paused");
  await expect(
    page.getByRole("button", { name: "Scan source" }),
  ).toBeDisabled();
  await page.screenshot({
    path: testInfo.outputPath("windows-control-plane.png"),
  });
  const scan = await request.post("/api/scan", { data: { rootId: "source" } });
  expect(scan.status()).toBe(409);
  expect((await scan.json()).error).toContain("Windows worker");
  const capabilities = await (
    await request.get("/api/media/capabilities")
  ).json();
  expect(capabilities.liveInspection).toBe(false);
  expect(capabilities.importedProbeReview).toBe(true);
  expect(capabilities.presetPreview).toBe(true);
  const processing = await (await request.get("/api/media/processing")).json();
  expect(processing.sampleRemux).toBe(false);
  expect(processing.sampleEncode).toBe(false);
  expect(
    (await request.get("/api/media/samples/invented/content")).status(),
  ).toBe(404);
  expect(
    (await request.get("/api/worker-qualification/invented/command")).status(),
  ).toBe(404);
  await page.getByRole("button", { name: "Media review", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Know your media" }),
  ).toBeVisible();
});
