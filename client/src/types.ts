export type Title = {
  id: number;
  name: string;
  kind: "movie" | "tv";
  year: number | null;
  plot: string | null;
  genre: string | null;
  lockedFields: string;
  revision: number;
};
export type Copy = {
  id: number;
  assetId: number;
  rootId: string;
  relativePath: string;
  role: string;
  state: string;
  bytes: number;
  modifiedTicks: number;
  firstSeenUtc: string;
  sha256: string | null;
  discId: string | null;
};
export type Row = {
  title: Title;
  edition: {
    id: number;
    name: string;
    season: number | null;
    episode: number | null;
  };
  asset: { id: number; fileName: string; bytes: number; sha256: string | null };
  copy: Copy;
  reviewEligible: boolean;
};
export type Root = {
  id: string;
  label: string;
  containerPath: string;
  windowsPath: string;
  role: string;
  online: boolean;
};
export type Preferences = {
  id: string;
  seedRetentionDays: number;
  discCapacityBytes: number;
  discReserveBytes: number;
};
export type Settings = { preferences: Preferences; roots: Root[] };
export type Job = {
  id: string;
  type: string;
  status: string;
  payload: string;
  error: string | null;
  progressBytes: number;
  cancelRequested: boolean;
  createdUtc: string;
};
export type Disc = {
  id: string;
  label: string;
  status: string;
  capacityBytes: number;
  reserveBytes: number;
  manifest: string;
};
export type Entry = {
  assetId: number;
  copyId: number;
  path: string;
  bytes: number;
  sha256: string;
};
export type Edit = { mode: "leave" | "set" | "clear"; value: string | null };
export type Change = {
  titleId: number;
  revision: number;
  fields: Record<string, Edit>;
  lockedFields: string[];
};
export type MetaPreview = {
  job: Job;
  rows: {
    before: Title;
    after: Title;
    paths: { oldPath: string; newPath: string }[];
  }[];
};
export type CopyPreview = {
  job: Job;
  plan: {
    sourceCopyId: number;
    targetRootId: string;
    targetPath: string;
    bytes: number;
    modifiedTicks: number;
  };
  sourcePath: string;
  freeBytes: number;
};
export type DiscPreview = {
  job: Job;
  plan: { capacityBytes: number; reserveBytes: number; bins: Entry[][] };
};
export const bytes = (n: number) =>
  n >= 1e12
    ? `${(n / 1e12).toFixed(2)} TB`
    : n >= 1e9
      ? `${(n / 1e9).toFixed(2)} GB`
      : n >= 1e6
        ? `${(n / 1e6).toFixed(1)} MB`
        : `${n.toLocaleString()} B`;
export async function api<T>(
  path: string,
  body?: unknown,
  method?: string,
): Promise<T> {
  const response = await fetch("/api" + path, {
    method: method ?? (body === undefined ? "GET" : "POST"),
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const result = await response
      .json()
      .catch(() => ({ error: "Service unavailable" }));
    throw new Error(result.error ?? `Request failed (${response.status})`);
  }
  const text = await response.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}
