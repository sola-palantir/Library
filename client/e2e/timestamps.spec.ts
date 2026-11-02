import { expect, test } from "@playwright/test";

test.use({ timezoneId: "America/Chicago", locale: "en-US" });

test("UTC catalog times display in Central time across the DST transition", async ({
  page,
}) => {
  const times = [
    [
      "before-clock-change",
      "2026-11-01T06:30:00Z",
      "11/1/2026, 1:30:00 AM CDT",
    ],
    ["after-clock-change", "2026-11-01T07:30:00Z", "11/1/2026, 1:30:00 AM CST"],
    [
      "reported-job",
      "2026-11-02T00:32:50.4986017Z",
      "11/1/2026, 6:32:50 PM CST",
    ],
  ];
  await page.route("**/api/jobs", (route) =>
    route.fulfill({
      json: times.map(([id, createdUtc]) => ({
        id,
        createdUtc,
        type: "read-only-scan",
        status: "completed",
        payload: "{}",
        progressJson: "{}",
        progressBytes: 0,
        cancelRequested: false,
      })),
    }),
  );
  await page.goto("/");
  await page.getByRole("button", { name: "Jobs", exact: true }).click();
  for (const [, , display] of times)
    await expect(page.getByText(display, { exact: false })).toBeVisible();
});
