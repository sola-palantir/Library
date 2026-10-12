import { useEffect, useRef, useState } from "react";
import { Archive, Disc3, Download, ShieldCheck, Search } from "lucide-react";
import { api, bytes } from "./types";
import type { Row, Settings } from "./types";
import "./backups.css";

type Bundle = {
  id: number;
  title: string;
  episode?: number;
  files: { path: string; bytes: number; sha256: string; sidecar: boolean }[];
};
type Coverage = {
  expected: number[];
  selected: number[];
  missing: number[];
  excluded: number[];
  entireSeasonIncluded: boolean;
};
type Preview = {
  job: { id: string };
  preview: {
    sha256: string;
    request: { capacityBytes: number; reserveBytes: number };
    packing: {
      discs: Bundle[][];
      unselectedBundleIds: number[];
      optimalityProven: boolean;
      nodes: number;
      method: string;
    };
    coverage: Coverage | null;
  };
};
type BackupDisc = {
  id: string;
  setId: string;
  label: string;
  ordinal: number;
  sha256: string;
  manifestJson: string;
};
type Evidence = {
  id?: string;
  discId: string;
  state: string;
  atUtc: string;
  detailJson: string;
};
type History = {
  sets: { id: string; name: string; snapshotJson: string }[];
  discs: BackupDisc[];
  evidence: Evidence[];
  coverage: {
    setId: string;
    scope: Coverage | null;
    verifiedDiscs: number;
    plannedDiscs: number;
    selectedScopeVerified: boolean;
    entireSeasonVerified: boolean;
  }[];
};
const failure = (e: unknown) => (e instanceof Error ? e.message : String(e));
function episodes(text: string): number[] {
  if (!text.trim()) return [];
  const values = text.split(/[\s,]+/).flatMap((part) => {
    const match = /^(\d+)(?:-(\d+))?$/.exec(part);
    if (!match)
      throw new Error("Episodes must be numbers or ranges, such as 1-8, 10.");
    const first = Number(match[1]),
      last = Number(match[2] ?? match[1]);
    if (first < 1 || last < first || last > 10000)
      throw new Error(
        "Episode ranges must be ascending, between 1 and 10,000.",
      );
    return Array.from({ length: last - first + 1 }, (_, i) => first + i);
  });
  return [...new Set(values)].sort((a, b) => a - b);
}
function CoverageView({ coverage }: { coverage: Coverage }) {
  return (
    <div className="backup-coverage">
      <strong>
        Declared season scope · {coverage.selected.length} selected /{" "}
        {coverage.expected.length} expected
      </strong>
      <p>Expected: {coverage.expected.join(", ") || "None declared"}</p>
      <p>Selected: {coverage.selected.join(", ") || "None"}</p>
      <p className={coverage.missing.length ? "backup-warning" : ""}>
        Missing: {coverage.missing.join(", ") || "None in declared scope"}
      </p>
      <p>Explicitly excluded: {coverage.excluded.join(", ") || "None"}</p>
      <small>
        Scope coverage is not proof of a verified season backup. Every assigned
        disc still requires physical read-back.
      </small>
    </div>
  );
}

export function BackupWorkspace({
  selected,
  settings,
  onChanged,
}: {
  selected: Row[];
  settings: Settings | null;
  onChanged?: () => void;
}) {
  const [name, setName] = useState("Media backup"),
    [objective, setObjective] = useState("minimize-discs"),
    [capacity, setCapacity] = useState("50000000000"),
    [reserve, setReserve] = useState("1000000000");
  const [seasonEnabled, setSeasonEnabled] = useState(false),
    [seasonTitle, setSeasonTitle] = useState(""),
    [seasonNumber, setSeasonNumber] = useState("1"),
    [expected, setExpected] = useState(""),
    [excluded, setExcluded] = useState(""),
    [sidecars, setSidecars] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null),
    [history, setHistory] = useState<History | null>(null),
    [query, setQuery] = useState(""),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false),
    [stageRoot, setStageRoot] = useState(""),
    [receipt, setReceipt] = useState("");
  const defaultsLoaded = useRef(false);
  useEffect(() => {
    if (settings && !defaultsLoaded.current) {
      defaultsLoaded.current = true;
      setCapacity(String(settings.preferences.discCapacityBytes));
      setReserve(String(settings.preferences.discReserveBytes));
      setStageRoot(settings.roots.find((r) => r.role === "staging")?.id ?? "");
    }
  }, [settings]);
  async function refresh(search = query) {
    setHistory(await api<History>(`/backups?q=${encodeURIComponent(search)}`));
  }
  useEffect(() => {
    let active = true;
    const timer = setTimeout(() => {
      api<History>(`/backups?q=${encodeURIComponent(query)}`)
        .then((h) => {
          if (active) setHistory(h);
        })
        .catch((e) => {
          if (active) setError(failure(e));
        });
    }, 180);
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [query]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (e) {
      setError(failure(e));
    } finally {
      setBusy(false);
    }
  }
  async function plan() {
    await run(async () => {
      setPreview(null);
      const capacityBytes = Number(capacity),
        reserveBytes = Number(reserve);
      if (
        !Number.isSafeInteger(capacityBytes) ||
        !Number.isSafeInteger(reserveBytes) ||
        capacityBytes <= reserveBytes ||
        reserveBytes < 0
      )
        throw new Error(
          "Enter valid capacity and reserve bytes; capacity must exceed reserve.",
        );
      const expectedEpisodes = episodes(expected),
        excludedEpisodes = episodes(excluded);
      if (
        seasonEnabled &&
        (!seasonTitle.trim() ||
          !expectedEpisodes.length ||
          !Number.isInteger(Number(seasonNumber)) ||
          Number(seasonNumber) < 0)
      )
        throw new Error(
          "Declare a season title, season number and expected episode scope.",
        );
      const bundledSidecars = sidecars
        .split("\n")
        .filter((x) => x.trim())
        .map((line) => {
          const colon = line.indexOf(":");
          const mediaCopyId = Number(line.slice(0, colon));
          const path = line.slice(colon + 1).trim();
          if (
            colon < 1 ||
            !Number.isInteger(mediaCopyId) ||
            !selected.some((r) => r.copy.id === mediaCopyId) ||
            !path
          )
            throw new Error(
              "Each sidecar line needs a selected copy ID and relative path: 12:Movie.nfo",
            );
          return { mediaCopyId, path };
        });
      setPreview(
        await api<Preview>("/backups/preview", {
          name,
          copyIds: selected.map((r) => r.copy.id),
          capacityBytes,
          reserveBytes,
          objective,
          sidecars: bundledSidecars,
          season: seasonEnabled
            ? {
                title: seasonTitle.trim(),
                season: Number(seasonNumber),
                expectedEpisodes,
                excludedEpisodes,
              }
            : undefined,
        }),
      );
    });
  }
  const selectionKey = selected
    .map((row) => `${row.copy.id}:${row.copy.modifiedTicks}:${row.copy.sha256}`)
    .sort()
    .join("|");
  useEffect(() => setPreview(null), [selectionKey]);
  const commandIds = useRef(new Map<string, string>());
  function commandId(operation: string): string {
    const existing = commandIds.current.get(operation);
    if (existing) return existing;
    const storageKey = `library.backup.command.${operation}`;
    let created = crypto.randomUUID() as string;
    try {
      created = sessionStorage.getItem(storageKey) ?? created;
      sessionStorage.setItem(storageKey, created);
    } catch {
      /* In-memory retry identity still works when storage is unavailable. */
    }
    commandIds.current.set(operation, created);
    return created;
  }
  const packing = preview?.preview.packing;
  return (
    <section className="backup-workspace" aria-label="Preservation backups">
      <div className="backup-heading">
        <div>
          <span className="backup-eyebrow">PRESERVATION WORKSPACE</span>
          <h2>
            <Archive size={23} /> Plan a backup that can be proved
          </h2>
          <p>
            Frozen content, permanent disc labels and an evidence trail for
            every attempt.
          </p>
        </div>
        <span className="backup-pending">Hardware qualification pending</span>
      </div>
      <div className="backup-callout">
        <ShieldCheck size={20} />
        <p>
          Planning never burns or changes source media. Nominal BD50 is 50 GB
          (46.57 GiB); measured media sectors and the final UDF image decide
          whether a burn fits. Only blank supported write-once media is
          eligible.
        </p>
      </div>
      {error && (
        <p className="backup-error" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="backup-notice" role="status">
          {notice}
        </p>
      )}
      <div className="backup-columns">
        <section className="backup-card">
          <h3>1. Declare the collection</h3>
          <p>
            {selected.length} selected library{" "}
            {selected.length === 1 ? "copy" : "copies"} ·{" "}
            {bytes(selected.reduce((sum, r) => sum + r.copy.bytes, 0))}
          </p>
          <p className="backup-muted">
            Select verified library copies in the table. Each movie or episode
            stays whole with its explicitly selected sidecars.
          </p>
          <label>
            Backup name
            <input
              value={name}
              onChange={(e) => {
                setName(e.target.value);
                setPreview(null);
              }}
            />
          </label>
          <label>
            Packing objective
            <select
              value={objective}
              onChange={(e) => {
                setObjective(e.target.value);
                setPreview(null);
              }}
            >
              <option value="minimize-discs">
                Minimize discs for this collection
              </option>
              <option value="fill-one">
                Fill one disc from this selection
              </option>
            </select>
          </label>
          <div className="backup-fields">
            <label>
              Planning capacity · bytes
              <input
                inputMode="numeric"
                value={capacity}
                onChange={(e) => {
                  setCapacity(e.target.value);
                  setPreview(null);
                }}
              />
            </label>
            <label>
              Reserved space · bytes
              <input
                inputMode="numeric"
                value={reserve}
                onChange={(e) => {
                  setReserve(e.target.value);
                  setPreview(null);
                }}
              />
            </label>
          </div>
          <label className="backup-check">
            <input
              type="checkbox"
              checked={seasonEnabled}
              onChange={(e) => {
                setSeasonEnabled(e.target.checked);
                setPreview(null);
              }}
            />{" "}
            Declare a TV season scope
          </label>
          {seasonEnabled && (
            <div className="backup-season">
              <div className="backup-fields">
                <label>
                  Series title
                  <input
                    value={seasonTitle}
                    onChange={(e) => {
                      setSeasonTitle(e.target.value);
                      setPreview(null);
                    }}
                  />
                </label>
                <label>
                  Season number
                  <input
                    type="number"
                    min="0"
                    value={seasonNumber}
                    onChange={(e) => {
                      setSeasonNumber(e.target.value);
                      setPreview(null);
                    }}
                  />
                </label>
              </div>
              <label>
                Expected episodes
                <input
                  placeholder="1-10, 12"
                  value={expected}
                  onChange={(e) => {
                    setExpected(e.target.value);
                    setPreview(null);
                  }}
                />
              </label>
              <label>
                Explicit exclusions
                <input
                  placeholder="11"
                  value={excluded}
                  onChange={(e) => {
                    setExcluded(e.target.value);
                    setPreview(null);
                  }}
                />
              </label>
              <small>
                Whole episodes may span multiple discs. Missing and excluded
                episodes remain visible in the frozen scope.
              </small>
            </div>
          )}
          <details>
            <summary>Associated sidecars</summary>
            <p>
              One selected copy ID and relative sidecar path per line. These
              files travel with their media bundle.
            </p>
            <textarea
              aria-label="Selected sidecars"
              rows={3}
              placeholder="12:Movies/Northlight/Northlight.nfo"
              value={sidecars}
              onChange={(e) => {
                setSidecars(e.target.value);
                setPreview(null);
              }}
            />
            <p className="backup-muted">
              Selected IDs:{" "}
              {selected.map((r) => r.copy.id).join(", ") || "None"}
            </p>
          </details>
          <button
            className="primary"
            disabled={busy || !selected.length || !name.trim()}
            onClick={plan}
          >
            Preview whole-file packing
          </button>
        </section>
        <section className="backup-card">
          <h3>2. Review the frozen plan</h3>
          {!preview ? (
            <div className="backup-empty">
              <Disc3 size={36} />
              <strong>Your disc map appears here</strong>
              <p>
                Preview exact file sizes, season coverage and unselected bundles
                before recording a backup.
              </p>
            </div>
          ) : (
            <>
              <p>
                {packing?.method} ·{" "}
                {packing?.optimalityProven
                  ? "Optimality proved for this objective"
                  : "Bounded result; optimality not proved"}
              </p>
              {preview.preview.coverage && (
                <CoverageView coverage={preview.preview.coverage} />
              )}
              {packing?.discs.map((bundles, i) => {
                const total = bundles.reduce(
                  (sum, b) => sum + b.files.reduce((n, f) => n + f.bytes, 0),
                  0,
                );
                const available =
                  preview.preview.request.capacityBytes -
                  preview.preview.request.reserveBytes;
                return (
                  <div className="backup-disc-plan" key={i}>
                    <div>
                      <strong>Disc {i + 1}</strong>
                      <span>
                        {bytes(total)} ·{" "}
                        {((total / available) * 100).toFixed(1)}% payload budget
                      </span>
                    </div>
                    <progress
                      value={total}
                      max={available}
                      aria-label={`Disc ${i + 1} planning utilization`}
                    />
                    <ul>
                      {bundles.map((b) => (
                        <li key={b.id}>
                          <strong>{b.title}</strong>
                          {b.episode != null && (
                            <span>Episode {b.episode}</span>
                          )}
                          <small>
                            {b.files.length} files ·{" "}
                            {b.files
                              .reduce((sum, f) => sum + f.bytes, 0)
                              .toLocaleString()}{" "}
                            bytes
                          </small>
                        </li>
                      ))}
                    </ul>
                  </div>
                );
              })}
              <p>
                Unselected bundles:{" "}
                {packing?.unselectedBundleIds.join(", ") || "None"}
              </p>
              <details>
                <summary>Frozen plan checksum</summary>
                <code>{preview.preview.sha256}</code>
              </details>
              <button
                className="primary"
                disabled={busy}
                onClick={() =>
                  run(async () => {
                    await api(`/backups/${preview.job.id}/approve`, {
                      commandId: commandId(`approve:${preview.job.id}`),
                    });
                    setPreview(null);
                    await refresh();
                    setNotice(
                      "Backup recorded. Stage each disc before preparing a Windows companion command.",
                    );
                    onChanged?.();
                  })
                }
              >
                Record this backup plan
              </button>
            </>
          )}
        </section>
      </div>
      <section className="backup-card">
        <div className="backup-history-title">
          <h3>3. Disc history & evidence</h3>
          <label className="backup-search">
            <Search size={16} />
            <input
              aria-label="Search backup history"
              placeholder="Search label, title or season"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </label>
          <button disabled={busy} onClick={() => run(() => refresh())}>
            Refresh history
          </button>
        </div>
        <label>
          Writable staging root
          <select
            value={stageRoot}
            onChange={(e) => setStageRoot(e.target.value)}
          >
            <option value="">Choose a staging root</option>
            {settings?.roots
              .filter((r) => r.role === "staging")
              .map((r) => (
                <option value={r.id} key={r.id}>
                  {r.label}
                </option>
              ))}
          </select>
        </label>
        {history?.coverage
          .filter((c) => c.scope !== null)
          .map((c) => (
            <section className="backup-coverage" key={c.setId}>
              <h4>
                {history.sets.find((s) => s.id === c.setId)?.name ??
                  "Season backup"}
              </h4>
              {c.scope && <CoverageView coverage={c.scope} />}
              <p>
                {c.verifiedDiscs} / {c.plannedDiscs} discs physically verified
              </p>
              <p>
                {c.entireSeasonVerified
                  ? "Entire declared season verified"
                  : c.selectedScopeVerified
                    ? "Selected scope verified; entire season remains incomplete"
                    : "Backup verification incomplete"}
              </p>
            </section>
          ))}
        {!history ? (
          <p>Loading backup history…</p>
        ) : !history.discs.length ? (
          <div className="backup-empty">
            <Archive size={28} />
            <p>
              No recorded discs match this search. Recorded labels and failed
              attempts remain in the history.
            </p>
          </div>
        ) : (
          <div className="backup-history">
            {history.discs.map((d) => {
              const evidence = history.evidence
                .filter((e) => e.discId === d.id)
                .sort((a, b) => b.atUtc.localeCompare(a.atUtc));
              return (
                <article key={d.id} className="backup-history-disc">
                  <header>
                    <div>
                      <span className="backup-label">{d.label}</span>
                      <p>
                        {history.sets.find((s) => s.id === d.setId)?.name ??
                          "Backup"}{" "}
                        · Disc {d.ordinal}
                      </p>
                    </div>
                    <span className="backup-state">
                      {evidence[0]?.state ?? "planned"}
                    </span>
                  </header>
                  <div className="backup-actions">
                    <button
                      disabled={busy || !stageRoot}
                      onClick={() =>
                        run(async () => {
                          await api(`/backups/discs/${d.id}/stage`, {
                            rootId: stageRoot,
                          });
                          setNotice(
                            "Independent staging job queued. Track its verification in Jobs, then refresh history.",
                          );
                          onChanged?.();
                        })
                      }
                    >
                      Stage verified files
                    </button>
                    <a
                      className="button"
                      href={`/api/backups/discs/${d.id}/manifest`}
                      download
                    >
                      <Download size={14} /> Manifest
                    </a>
                    <a
                      className="button"
                      href={`/api/backups/discs/${d.id}/command`}
                      download
                    >
                      <Download size={14} /> Companion command
                    </a>
                  </div>
                  <details>
                    <summary>
                      Permanent identity & attempt evidence ({evidence.length})
                    </summary>
                    <p>
                      Disc ID <code>{d.id}</code>
                    </p>
                    <p>
                      Manifest SHA-256 <code>{d.sha256}</code>
                    </p>
                    {evidence.map((e, i) => (
                      <div className="backup-event" key={e.id ?? i}>
                        <strong>{e.state}</strong>
                        <time>{new Date(e.atUtc).toLocaleString()}</time>
                        <pre>{e.detailJson}</pre>
                      </div>
                    ))}
                    <p>
                      Replacements receive a new permanent identity; the
                      previous disc and its evidence remain recorded.
                    </p>
                    <button
                      disabled={busy}
                      onClick={() =>
                        run(async () => {
                          await api(`/backups/discs/${d.id}/replacement`, {
                            commandId: commandId(`replacement:${d.id}`),
                          });
                          await refresh();
                          setNotice(
                            "Replacement disc recorded with a new permanent label.",
                          );
                        })
                      }
                    >
                      Record replacement disc
                    </button>
                  </details>
                </article>
              );
            })}
          </div>
        )}
        <details className="backup-receipts">
          <summary>Import signed companion evidence</summary>
          <p>
            Import the companion's signed JSON envelope. Reported writing alone
            never proves read-back verification.
          </p>
          <input
            aria-label="Signed evidence file"
            type="file"
            accept="application/json,.json"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file)
                run(async () => {
                  if (file.size > 2_000_000)
                    throw new Error("Evidence file exceeds 2 MB.");
                  setReceipt(await file.text());
                });
            }}
          />
          <textarea
            aria-label="Signed companion evidence"
            rows={5}
            value={receipt}
            onChange={(e) => setReceipt(e.target.value)}
            placeholder='{"payload": "…", "signature": "…"}'
          />
          <button
            disabled={busy || !receipt.trim()}
            onClick={() =>
              run(async () => {
                const envelope = JSON.parse(receipt);
                if (!envelope.payload || !envelope.signature)
                  throw new Error(
                    "Signed evidence requires payload and signature.",
                  );
                await api("/backups/receipts", envelope);
                setReceipt("");
                await refresh();
                setNotice(
                  "Signed evidence accepted. Review the recorded state and attempt details above.",
                );
              })
            }
          >
            Validate and import evidence
          </button>
        </details>
      </section>
    </section>
  );
}

export function CompanionSettings() {
  const [status, setStatus] = useState<{
      configured: boolean;
      keyFingerprint: string | null;
    } | null>(null),
    [pem, setPem] = useState(""),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false);
  useEffect(() => {
    api<{ configured: boolean; keyFingerprint: string | null }>(
      "/backups/companion",
    )
      .then(setStatus)
      .catch((e) => setError(failure(e)));
  }, []);
  return (
    <section className="backup-card backup-companion">
      <h3>Windows optical companion</h3>
      <p>
        The Docker UI plans and catalogs. The Windows companion measures media,
        builds the frozen UDF image and operates IMAPI; BurnAware can write that
        same image as a fallback.
      </p>
      <span className="backup-pending">Hardware qualification pending</span>
      <p>
        {status
          ? status.configured
            ? "Receipt verification key configured"
            : "No companion receipt key configured"
          : "Loading pairing status…"}
      </p>
      {status?.keyFingerprint && (
        <p>
          Public key fingerprint <code>{status.keyFingerprint}</code>
        </p>
      )}
      <p>
        Pair only a companion you control. Import its public key here; keep its
        private key on Windows. Pairing does not connect to a device or
        authorize a burn.
      </p>
      <label>
        Companion public key · PEM
        <textarea
          rows={5}
          value={pem}
          onChange={(e) => setPem(e.target.value)}
          placeholder="-----BEGIN PUBLIC KEY-----"
        />
      </label>
      <button
        disabled={busy || !pem.trim()}
        onClick={async () => {
          setBusy(true);
          setError("");
          setNotice("");
          try {
            await api("/backups/companion", { publicKeyPem: pem }, "PUT");
            setStatus(await api("/backups/companion"));
            setPem("");
            setNotice("Public key saved. No device connection was made.");
          } catch (e) {
            setError(failure(e));
          } finally {
            setBusy(false);
          }
        }}
      >
        Save receipt verification key
      </button>
      {error && (
        <p className="backup-error" role="alert">
          {error}
        </p>
      )}
      {notice && <p role="status">{notice}</p>}
    </section>
  );
}

type AuditPolicy = {
  enabled: boolean;
  intervalHours: number;
  maxFiles: number;
  maxBytes: number;
  cursor: number;
  lastRunUtc: string | null;
};
type PreservationEvent = { action: string; detail: string; atUtc: string };
type RepairPreview = {
  job: { id: string };
  plan: {
    sourceCopyId: number;
    targetRootId: string;
    targetPath: string;
    bytes?: number;
  };
  requiresApproval: boolean;
};
export function PreservationSettings() {
  const [policy, setPolicy] = useState<AuditPolicy | null>(null),
    [events, setEvents] = useState<PreservationEvent[]>([]),
    [roots, setRoots] = useState<Settings["roots"]>([]),
    [copyId, setCopyId] = useState(""),
    [healthyId, setHealthyId] = useState(""),
    [targetRoot, setTargetRoot] = useState(""),
    [targetPath, setTargetPath] = useState(""),
    [repair, setRepair] = useState<RepairPreview | null>(null),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false);
  async function history() {
    setEvents(await api<PreservationEvent[]>("/preservation/history"));
  }
  useEffect(() => {
    Promise.all([
      api<AuditPolicy>("/preservation/policy"),
      api<PreservationEvent[]>("/preservation/history"),
      api<Settings>("/settings"),
    ])
      .then(([p, h, s]) => {
        setPolicy(p);
        setEvents(h);
        setRoots(s.roots.filter((r) => r.role === "library"));
      })
      .catch((e) => setError(failure(e)));
  }, []);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (e) {
      setError(failure(e));
    } finally {
      setBusy(false);
    }
  }
  function requireId(value: string) {
    const id = Number(value);
    if (!Number.isSafeInteger(id) || id < 1)
      throw new Error("Enter a positive catalog copy ID.");
    return id;
  }
  return (
    <section className="backup-card backup-companion">
      <h3>Preservation audits & reviewed repairs</h3>
      <p>
        Bounded audits read content and record evidence. Failures create review
        work; they never delete media or automatically repair a file. Offline
        copies remain in the catalog.
      </p>
      {error && (
        <p className="backup-error" role="alert">
          {error}
        </p>
      )}
      {notice && <p role="status">{notice}</p>}
      {policy && (
        <>
          <label className="backup-check">
            <input
              type="checkbox"
              checked={policy.enabled}
              onChange={(e) =>
                setPolicy({ ...policy, enabled: e.target.checked })
              }
            />{" "}
            Enable periodic content audits
          </label>
          <div className="backup-fields">
            <label>
              Interval · hours
              <input
                type="number"
                min="1"
                value={policy.intervalHours}
                onChange={(e) =>
                  setPolicy({
                    ...policy,
                    intervalHours: Number(e.target.value),
                  })
                }
              />
            </label>
            <label>
              Maximum files per pass
              <input
                type="number"
                min="1"
                value={policy.maxFiles}
                onChange={(e) =>
                  setPolicy({ ...policy, maxFiles: Number(e.target.value) })
                }
              />
            </label>
            <label>
              Maximum bytes per pass
              <input
                type="number"
                min="1"
                value={policy.maxBytes}
                onChange={(e) =>
                  setPolicy({ ...policy, maxBytes: Number(e.target.value) })
                }
              />
            </label>
          </div>
          <p className="backup-muted">
            Last audit pass:{" "}
            {policy.lastRunUtc
              ? new Date(policy.lastRunUtc).toLocaleString()
              : "Not yet run"}{" "}
            · Catalog cursor {policy.cursor}
          </p>
          <button
            disabled={busy}
            onClick={() =>
              run(async () => {
                if (
                  ![
                    policy.intervalHours,
                    policy.maxFiles,
                    policy.maxBytes,
                  ].every((n) => Number.isSafeInteger(n) && n > 0)
                )
                  throw new Error(
                    "Audit limits must be positive whole numbers.",
                  );
                await api("/preservation/policy", policy, "PUT");
                setPolicy(await api("/preservation/policy"));
                setNotice(
                  "Audit policy saved. Repairs still require individual review.",
                );
              })
            }
          >
            Save audit policy
          </button>
        </>
      )}
      <details>
        <summary>Audit an individual copy now</summary>
        <label>
          Catalog copy ID
          <input
            inputMode="numeric"
            value={copyId}
            onChange={(e) => setCopyId(e.target.value)}
          />
        </label>
        <button
          disabled={busy || !copyId}
          onClick={() =>
            run(async () => {
              const result = await api<{
                outcome: string;
                error: string | null;
              }>(`/preservation/audit/${requireId(copyId)}`, {});
              await history();
              setNotice(
                `Audit outcome: ${result.outcome}${result.error ? ` — ${result.error}` : ""}. Review the recorded evidence below.`,
              );
            })
          }
        >
          Request content audit
        </button>
      </details>
      <details>
        <summary>Preview an independent repair copy</summary>
        <p>
          Choose a known healthy catalog copy and a new destination. The
          original remains untouched. Nothing is copied until you review and
          approve the plan.
        </p>
        <label>
          Healthy source copy ID
          <input
            inputMode="numeric"
            value={healthyId}
            onChange={(e) => {
              setHealthyId(e.target.value);
              setRepair(null);
            }}
          />
        </label>
        <label>
          Repair library root
          <select
            value={targetRoot}
            onChange={(e) => {
              setTargetRoot(e.target.value);
              setRepair(null);
            }}
          >
            <option value="">Choose a library root</option>
            {roots.map((r) => (
              <option value={r.id} key={r.id}>
                {r.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          New relative destination path
          <input
            value={targetPath}
            onChange={(e) => {
              setTargetPath(e.target.value);
              setRepair(null);
            }}
            placeholder="Recovered/Title/Title.mkv"
          />
        </label>
        <button
          disabled={busy || !healthyId || !targetRoot || !targetPath}
          onClick={() =>
            run(async () => {
              setRepair(null);
              setRepair(
                await api<RepairPreview>("/preservation/repair-preview", {
                  healthyCopyId: requireId(healthyId),
                  targetRootId: targetRoot,
                  targetPath,
                }),
              );
            })
          }
        >
          Preview repair copy
        </button>
        {repair && (
          <div className="backup-coverage">
            <h4>Review repair destination</h4>
            <p>
              Copy #{repair.plan.sourceCopyId} → {repair.plan.targetRootId}
            </p>
            <code>{repair.plan.targetPath}</code>
            <p>
              Independent copy with destination verification. Existing
              destination files are never overwritten.
            </p>
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                run(async () => {
                  await api(`/copy/${repair.job.id}/commit`, {});
                  setRepair(null);
                  setNotice(
                    "Reviewed repair queued. Follow the verified copy job in Jobs.",
                  );
                  await history();
                })
              }
            >
              Approve independent repair
            </button>
          </div>
        )}
      </details>
      <details>
        <summary>Audit & repair history ({events.length})</summary>
        <button disabled={busy} onClick={() => run(history)}>
          Refresh preservation history
        </button>
        {events.length === 0 ? (
          <p>No preservation events recorded.</p>
        ) : (
          events.map((event, i) => (
            <article className="backup-event" key={`${event.atUtc}-${i}`}>
              <strong>{event.action}</strong>
              <time>{new Date(event.atUtc).toLocaleString()}</time>
              <pre>{event.detail}</pre>
            </article>
          ))
        )}
      </details>
    </section>
  );
}
