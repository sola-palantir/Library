// API timestamps carry Z/an explicit offset. Render in the viewer's timezone,
// including its abbreviation so DST changes do not look like clock errors.
export const localTime = (value: string) =>
  new Date(value).toLocaleString(undefined, { timeZoneName: "short" });
