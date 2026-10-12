import { StrictMode, useCallback, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Archive,
  ArrowDownToLine,
  ArrowLeftRight,
  ArrowRight,
  Check,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  CircleHelp,
  Disc3,
  Download,
  FileVideo,
  Folder,
  HardDrive,
  Inbox,
  Layers3,
  ListVideo,
  LoaderCircle,
  LockKeyhole,
  Search,
  Settings2,
  ShieldCheck,
  SlidersHorizontal,
  X,
} from "lucide-react";
import { api, bytes } from "./types";
import type {
  Change,
  CopyPreview,
  Disc,
  DiscPreview,
  Edit,
  Entry,
  Job,
  MetaPreview,
  Preferences,
  Row,
  Settings,
} from "./types";
import "./style.css";
import {
  BackupWorkspace,
  CompanionSettings,
  PreservationSettings,
} from "./Backups";
import {
  IntegrationSettings,
  MetadataMatcher,
  QBitPanel,
} from "./Integrations";

type Area = "Library" | "Inbox" | "Organize" | "Discs" | "Jobs" | "Settings";
const areas = [
  { name: "Library", icon: ListVideo },
  { name: "Inbox", icon: Inbox },
  { name: "Organize", icon: ArrowLeftRight },
  { name: "Discs", icon: Disc3 },
  { name: "Jobs", icon: Layers3 },
  { name: "Settings", icon: Settings2 },
] as const;
const fields = ["name", "year", "genre", "plot"];
const emptyEdits = (): Record<string, Edit> =>
  Object.fromEntries(fields.map((f) => [f, { mode: "leave", value: null }]));
const statusLabel = (s: string) => s.replaceAll("-", " ");
function Badge({ value }: { value: string }) {
  return (
    <span className={`badge ${value}`}>
      {["verified", "completed", "read-back-verified"].includes(value) && (
        <Check size={11} />
      )}{" "}
      {statusLabel(value)}
    </span>
  );
}
function Artwork({ row, large = false }: { row: Row; large?: boolean }) {
  const hue = (row.title.name.charCodeAt(0) * 17 + row.title.id * 43) % 360;
  return (
    <div
      className={`art ${large ? "large" : ""}`}
      style={{ "--art-hue": hue } as React.CSSProperties}
      aria-hidden="true"
    >
      <span className="art-orbit" />
      <span className="art-mountain" />
      {large ? (
        <>
          <small>
            {row.title.kind === "tv" ? "A TELEVISION SERIES" : "MOTION PICTURE"}
          </small>
          <strong>{row.title.name}</strong>
          <em>{row.title.year ?? "CATALOG EDITION"}</em>
        </>
      ) : (
        <span className="art-initial">{row.title.name.slice(0, 1)}</span>
      )}
    </div>
  );
}
function App() {
  const [area, setArea] = useState<Area>("Library"),
    [settings, setSettings] = useState<Settings | null>(null),
    [rows, setRows] = useState<Row[]>([]),
    [total, setTotal] = useState(0),
    [query, setQuery] = useState(""),
    [search, setSearch] = useState(""),
    [kind, setKind] = useState(""),
    [page, setPage] = useState(0),
    [selected, setSelected] = useState<Set<number>>(new Set()),
    [focus, setFocus] = useState<Row | null>(null),
    [jobs, setJobs] = useState<Job[]>([]),
    [discs, setDiscs] = useState<Disc[]>([]),
    [summary, setSummary] = useState({
      titles: 0,
      copies: 0,
      libraryBytes: 0,
      sourceCopies: 0,
      discs: 0,
      activeJobs: 0,
    }),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false),
    [loading, setLoading] = useState(true),
    [inspectorWidth, setInspectorWidth] = useState(310);
  const [targetRoot, setTargetRoot] = useState(""),
    [targetPath, setTargetPath] = useState(""),
    [copyPreview, setCopyPreview] = useState<CopyPreview | null>(null),
    [discPreview, setDiscPreview] = useState<DiscPreview | null>(null),
    [metaOpen, setMetaOpen] = useState(false),
    [edits, setEdits] = useState<Record<string, Edit>>(emptyEdits()),
    [locks, setLocks] = useState<string[] | null>(null),
    [metaPreview, setMetaPreview] = useState<MetaPreview | null>(null),
    [nfoXml, setNfoXml] = useState<string | null>(null);
  const loadVersion = useRef(0);
  const searchRef = useRef<HTMLInputElement>(null);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const load = useCallback(async () => {
    const version = ++loadVersion.current;
    const role =
      area === "Inbox" || area === "Organize"
        ? "source"
        : area === "Discs"
          ? "library"
          : "";
    const [catalog, s, j, d, sum] = await Promise.all([
      api<{ items: Row[]; total: number }>(
        `/catalog?q=${encodeURIComponent(search)}&kind=${kind}&role=${role}&page=${page}`,
      ),
      api<Settings>("/settings"),
      api<Job[]>("/jobs"),
      api<Disc[]>("/discs"),
      api<typeof summary>("/summary"),
    ]);
    if (version !== loadVersion.current) return;
    setRows(catalog.items);
    setTotal(catalog.total);
    setSettings(s);
    setJobs(j);
    setDiscs(d);
    setSummary(sum);
    setLoading(false);
    setTargetRoot(
      (old) => old || s.roots.find((r) => r.role === "library")?.id || "",
    );
    setFocus((old) =>
      old
        ? (catalog.items.find((r) => r.copy.id === old.copy.id) ?? old)
        : null,
    );
  }, [area, search, kind, page]);
  useEffect(() => {
    if (query === search) return;
    const t = setTimeout(() => {
      setSearch(query);
      setPage(0);
      setSelected(new Set());
    }, 200);
    return () => clearTimeout(t);
  }, [query, search]);
  useEffect(() => {
    setLoading(true);
    load().catch((e) => {
      setError(e.message);
      setLoading(false);
    });
    const t = setInterval(() => load().catch(() => {}), 3000);
    return () => {
      clearInterval(t);
      loadVersion.current++;
    };
  }, [load]);
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (
        e.key === "/" &&
        !(
          e.target instanceof HTMLInputElement ||
          e.target instanceof HTMLTextAreaElement
        )
      ) {
        e.preventDefault();
        searchRef.current?.focus();
      }
      if (e.key === "Escape") {
        setFocus(null);
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);
  const modal = copyPreview || metaOpen || discPreview;
  useEffect(() => {
    if (modal) {
      dialogRef.current?.showModal();
    } else dialogRef.current?.close();
  }, [!!modal]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    try {
      await action();
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function navigate(next: Area) {
    setArea(next);
    setPage(0);
    setSelected(new Set());
    setFocus(null);
    setQuery("");
    setKind("");
    setNotice("");
  }
  function toggle(id: number) {
    setSelected((old) => {
      const next = new Set(old);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }
  function close() {
    setCopyPreview(null);
    setDiscPreview(null);
    setMetaOpen(false);
    setMetaPreview(null);
    setNfoXml(null);
  }
  async function previewCopy(row: Row) {
    await run(async () => {
      setCopyPreview(
        await api<CopyPreview>("/copy/preview", {
          sourceCopyId: row.copy.id,
          targetRootId: targetRoot,
          targetPath: targetPath || null,
        }),
      );
    });
  }
  const chosen = rows.filter((r) => selected.has(r.copy.id));
  const metadataRows = chosen.length ? chosen : focus ? [focus] : [];
  function openMetadata() {
    setEdits(emptyEdits());
    setLocks(null);
    setMetaPreview(null);
    setMetaOpen(true);
  }
  async function previewMetadata() {
    await run(async () => {
      const unique = [
        ...new Map(metadataRows.map((r) => [r.title.id, r])).values(),
      ];
      const changes: Change[] = unique.map((r) => ({
        titleId: r.title.id,
        revision: r.title.revision,
        fields: edits,
        lockedFields: locks ?? JSON.parse(r.title.lockedFields),
      }));
      setMetaPreview(await api<MetaPreview>("/metadata/preview", { changes }));
    });
  }
  async function exportNfo(row: Row) {
    await run(async () => {
      const response = await fetch("/api/nfo/export", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          titleId: row.title.id,
          editionId: row.edition.id,
          xml: nfoXml,
        }),
      });
      if (!response.ok) throw new Error((await response.json()).error);
      const url = URL.createObjectURL(await response.blob());
      const a = document.createElement("a");
      a.href = url;
      a.download = row.asset.fileName.replace(/\.[^.]+$/, ".nfo");
      a.click();
      URL.revokeObjectURL(url);
      setNotice("NFO downloaded. Source sidecars remain untouched.");
    });
  }
  const table = (data: Row[], compact = false) => (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            <th className="check">
              <input
                type="checkbox"
                aria-label="Select this page"
                checked={
                  data.length > 0 && data.every((r) => selected.has(r.copy.id))
                }
                onChange={(e) =>
                  setSelected(
                    e.target.checked
                      ? new Set(data.map((r) => r.copy.id))
                      : new Set(),
                  )
                }
              />
            </th>
            <th>
              Title <span className="sort">↑</span>
            </th>
            {!compact && <th>Type</th>}
            <th>Size</th>
            <th>Copy status</th>
            {!compact && <th>Location</th>}
          </tr>
        </thead>
        <tbody>
          {data.map((row) => (
            <tr
              key={row.copy.id}
              className={focus?.copy.id === row.copy.id ? "focused" : ""}
              draggable={area === "Organize"}
              onDragStart={(e) =>
                e.dataTransfer.setData(
                  "application/x-library-copy",
                  String(row.copy.id),
                )
              }
              onClick={() => setFocus(row)}
            >
              <td className="check" onClick={(e) => e.stopPropagation()}>
                <input
                  aria-label={`Select ${row.title.name}`}
                  type="checkbox"
                  checked={selected.has(row.copy.id)}
                  onChange={() => toggle(row.copy.id)}
                />
              </td>
              <td>
                <button className="title-button" onClick={() => setFocus(row)}>
                  <Artwork row={row} />
                  <span>
                    <strong>{row.title.name}</strong>
                    <small>
                      {row.title.year ?? "Year unknown"} <span>·</span>{" "}
                      {row.edition.season !== null
                        ? `S${String(row.edition.season).padStart(2, "0")} E${String(row.edition.episode).padStart(2, "0")}`
                        : row.edition.name}
                    </small>
                  </span>
                </button>
              </td>
              {!compact && (
                <td>
                  <span className="type">
                    {row.title.kind === "tv" ? "TV series" : "Movie"}
                  </span>
                </td>
              )}
              <td className="numeric">{bytes(row.copy.bytes)}</td>
              <td>
                <Badge value={row.copy.state} />
                {row.reviewEligible && (
                  <small className="eligible">Retention review eligible</small>
                )}
              </td>
              {!compact && (
                <td>
                  <span className="location">
                    <HardDrive size={13} />
                    {row.copy.role === "disc"
                      ? "Offline disc"
                      : row.copy.role === "source"
                        ? "Source retained"
                        : "Library"}
                  </span>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      {!data.length && (
        <div className="empty">
          <Archive size={38} />
          <h3>
            {loading
              ? "Loading your catalog…"
              : search
                ? "No matching media"
                : "A home for every copy"}
          </h3>
          <p>
            {search
              ? "Try a different title or clear your filters."
              : "Scan a configured source to bring movies and TV into your catalog. Files stay exactly where they are."}
          </p>
          {!search && (
            <button onClick={() => navigate("Settings")}>
              Configure storage <ArrowRight size={15} />
            </button>
          )}
        </div>
      )}
    </div>
  );
  return (
    <div className="app">
      <aside className="sidebar">
        <a
          className="brand"
          href="#"
          onClick={(e) => {
            e.preventDefault();
            navigate("Library");
          }}
        >
          <span className="brand-icon">
            <Layers3 size={22} />
          </span>
          Library<span className="brand-dot">.</span>
        </a>
        <div className="workspace-label">PERSONAL MEDIA WORKSPACE</div>
        <nav aria-label="Main navigation">
          {areas.map(({ name, icon: Icon }) => (
            <button
              key={name}
              aria-label={name}
              className={area === name ? "active" : ""}
              onClick={() => navigate(name)}
            >
              <Icon size={18} />
              <span>{name}</span>
              {name === "Inbox" && summary.sourceCopies > 0 && (
                <b>{summary.sourceCopies}</b>
              )}
              {name === "Jobs" && summary.activeJobs > 0 && (
                <b>{summary.activeJobs}</b>
              )}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="safe-note">
            <ShieldCheck size={18} />
            <div>
              Made to preserve<small>Originals always stay put</small>
            </div>
          </div>
          <div className="profile">
            <span>M</span>
            <div>
              Micah’s library<small>Local workspace</small>
            </div>
            <span className="online-dot" />
          </div>
        </div>
      </aside>
      <main>
        <header className="topbar">
          <div className="breadcrumb">
            Workspace <span>/</span> <strong>{area}</strong>
          </div>
          <div className="topbar-right">
            <span className="local">
              <span className="online-dot" /> Local catalog
            </span>
            <button
              className="icon-button"
              aria-label="Open settings"
              onClick={() => navigate("Settings")}
            >
              <CircleHelp size={18} />
            </button>
          </div>
        </header>
        <div className="page">
          <div className="page-heading">
            <div>
              <div className="eyebrow">YOUR COLLECTION, CONNECTED</div>
              <h1>
                {area === "Library"
                  ? "The library"
                  : area === "Inbox"
                    ? "Incoming media"
                    : area === "Organize"
                      ? "A place for everything"
                      : area === "Discs"
                        ? "The disc archive"
                        : area === "Jobs"
                          ? "Work in progress"
                          : "Workspace settings"}
              </h1>
              <p>
                {area === "Library"
                  ? "Every title. Every copy. One clear picture."
                  : area === "Inbox"
                    ? "Review downloads without disturbing the files you’re seeding."
                    : area === "Organize"
                      ? "Preview the destination. Copy independently. Verify every byte."
                      : area === "Discs"
                        ? "Whole files, thoughtfully packed. Ready for the shelf."
                        : area === "Jobs"
                          ? "Durable operations, with a history you can trust."
                          : "Explicit roots, sensible defaults and a portable catalog."}
              </p>
            </div>
            {["Library", "Inbox"].includes(area) && (
              <button
                className="primary"
                disabled={
                  busy ||
                  !settings?.roots.some((r) => r.role === "source" && r.online)
                }
                onClick={() =>
                  run(async () => {
                    const root = settings?.roots.find(
                      (r) => r.role === "source" && r.online,
                    );
                    if (root) {
                      await api("/scan", { rootId: root.id });
                      setNotice(
                        "Read-only scan queued. Follow its progress in Jobs.",
                      );
                    }
                  })
                }
              >
                <Download size={16} /> Scan source
              </button>
            )}
          </div>
          {error && (
            <div className="alert error" role="alert">
              <span>{error}</span>
              <button
                className="icon-button"
                onClick={() => setError("")}
                aria-label="Dismiss error"
              >
                <X size={16} />
              </button>
            </div>
          )}
          {notice && (
            <div className="alert" role="status">
              <CheckCircle2 size={16} />
              <span>{notice}</span>
              <button
                className="icon-button"
                onClick={() => setNotice("")}
                aria-label="Dismiss notification"
              >
                <X size={16} />
              </button>
            </div>
          )}
          {area === "Library" && (
            <div className="stats">
              <div>
                <span>Cataloged titles</span>
                <strong>
                  {summary.titles.toLocaleString()}
                  <ListVideo size={18} />
                </strong>
                <small>Movies & television</small>
              </div>
              <div>
                <span>Library storage</span>
                <strong>
                  {bytes(summary.libraryBytes)}
                  <HardDrive size={18} />
                </strong>
                <small>Independent library copies</small>
              </div>
              <div>
                <span>Retained sources</span>
                <strong>
                  {summary.sourceCopies.toLocaleString()}
                  <ShieldCheck size={18} />
                </strong>
                <small>
                  {settings?.preferences.seedRetentionDays ?? 20}-day review
                  window
                </small>
              </div>
              <div>
                <span>Archive discs</span>
                <strong>
                  {summary.discs.toLocaleString()}
                  <Disc3 size={18} />
                </strong>
                <small>Physical copies, still cataloged</small>
              </div>
            </div>
          )}
          {area === "Inbox" && <QBitPanel />}
          {["Library", "Inbox", "Organize", "Discs"].includes(area) && (
            <div className="toolbar">
              <div className="tabs" aria-label="Media type">
                {[
                  ["", "All media"],
                  ["movie", "Movies"],
                  ["tv", "TV series"],
                ].map(([value, label]) => (
                  <button
                    className={kind === value ? "selected" : ""}
                    key={value}
                    onClick={() => {
                      setKind(value);
                      setPage(0);
                      setSelected(new Set());
                    }}
                  >
                    {label}
                    {value === "" && <span>{total}</span>}
                  </button>
                ))}
              </div>
              <div className="search">
                <Search size={15} />
                <input
                  ref={searchRef}
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Search your collection…"
                  aria-label="Search media"
                />
                <kbd>/</kbd>
              </div>
            </div>
          )}
          {selected.size > 0 &&
            ["Library", "Inbox", "Organize", "Discs"].includes(area) && (
              <div className="selection-bar">
                <span>{selected.size} selected on this page</span>
                <button onClick={openMetadata}>
                  <SlidersHorizontal size={14} /> Edit metadata
                </button>
                {area === "Discs" && (
                  <button
                    disabled={busy}
                    onClick={() =>
                      run(async () => {
                        setDiscPreview(
                          await api<DiscPreview>("/discs/preview", {
                            copyIds: [...selected],
                            capacityBytes:
                              settings!.preferences.discCapacityBytes,
                            reserveBytes:
                              settings!.preferences.discReserveBytes,
                          }),
                        );
                      })
                    }
                  >
                    <Disc3 size={14} /> Preview disc batches
                  </button>
                )}
                <button
                  className="subtle"
                  onClick={() => setSelected(new Set())}
                >
                  Clear selection
                </button>
              </div>
            )}
          {["Library", "Inbox"].includes(area) && (
            <div
              className="catalog-layout"
              style={
                {
                  "--inspector-width": `${inspectorWidth}px`,
                } as React.CSSProperties
              }
            >
              <section className="catalog-panel">
                {table(rows)}
                <div className="table-footer">
                  <span>
                    {total
                      ? `${page * 50 + 1}–${Math.min((page + 1) * 50, total)} of ${total} copies`
                      : "0 copies"}
                    <span className="footer-dot">·</span>No files moved
                  </span>
                  <div>
                    <button
                      className="icon-button"
                      aria-label="Previous page"
                      disabled={page === 0}
                      onClick={() => {
                        setPage((p) => p - 1);
                        setSelected(new Set());
                      }}
                    >
                      <ChevronLeft size={16} />
                    </button>
                    <button
                      className="icon-button"
                      aria-label="Next page"
                      disabled={(page + 1) * 50 >= total}
                      onClick={() => {
                        setPage((p) => p + 1);
                        setSelected(new Set());
                      }}
                    >
                      <ChevronRight size={16} />
                    </button>
                  </div>
                </div>
              </section>
              {focus && (
                <>
                  <div
                    className="resize-handle"
                    role="separator"
                    aria-label="Resize inspector"
                    aria-orientation="vertical"
                    aria-valuenow={inspectorWidth}
                    aria-valuemin={250}
                    aria-valuemax={500}
                    tabIndex={0}
                    onKeyDown={(e) => {
                      if (e.key === "ArrowLeft")
                        setInspectorWidth((w) => Math.min(500, w + 20));
                      if (e.key === "ArrowRight")
                        setInspectorWidth((w) => Math.max(250, w - 20));
                    }}
                    onPointerDown={(e) => {
                      e.currentTarget.setPointerCapture(e.pointerId);
                    }}
                    onPointerMove={(e) => {
                      if (e.currentTarget.hasPointerCapture(e.pointerId))
                        setInspectorWidth((w) =>
                          Math.max(250, Math.min(500, w - e.movementX)),
                        );
                    }}
                  />
                  <aside className="inspector">
                    <div className="inspector-head">
                      <span>MEDIA DETAILS</span>
                      <button
                        className="icon-button"
                        aria-label="Close inspector"
                        onClick={() => setFocus(null)}
                      >
                        <X size={16} />
                      </button>
                    </div>
                    <Artwork row={focus} large />
                    <div className="inspector-body">
                      <h2>{focus.title.name}</h2>
                      <p>
                        {focus.title.year ?? "Year unknown"} ·{" "}
                        {focus.title.kind === "tv" ? "TV series" : "Movie"} ·{" "}
                        {focus.edition.name}
                      </p>
                      <Badge value={focus.copy.state} />
                      <p className="plot">
                        {focus.title.plot ||
                          "Add a synopsis, year or genre to make this title yours."}
                      </p>
                      <h3>THIS COPY</h3>
                      <dl>
                        <dt>Location</dt>
                        <dd>
                          {focus.copy.role === "source"
                            ? "Retained source"
                            : focus.copy.role}
                        </dd>
                        <dt>Size</dt>
                        <dd>{bytes(focus.copy.bytes)}</dd>
                        <dt>Verification</dt>
                        <dd>
                          {focus.copy.sha256
                            ? "SHA-256 recorded"
                            : "Not yet hashed"}
                        </dd>
                        <dt>First observed</dt>
                        <dd>
                          {new Date(
                            focus.copy.firstSeenUtc,
                          ).toLocaleDateString()}
                        </dd>
                      </dl>
                      <div className="path">
                        <Folder size={14} />
                        <code>{focus.copy.relativePath}</code>
                      </div>
                      {focus.copy.sha256 && (
                        <details>
                          <summary>SHA-256 checksum</summary>
                          <code className="hash">{focus.copy.sha256}</code>
                        </details>
                      )}
                      <MetadataMatcher
                        row={focus}
                        onPreview={(preview) => {
                          setSelected(new Set());
                          setEdits(
                            JSON.parse(preview.job.payload).changes[0].fields,
                          );
                          setLocks(
                            JSON.parse(preview.job.payload).changes[0]
                              .lockedFields,
                          );
                          setMetaPreview(preview);
                          setMetaOpen(true);
                          setNfoXml(null);
                        }}
                      />
                      <div className="inspector-actions">
                        <button onClick={openMetadata}>
                          <SlidersHorizontal size={14} /> Edit metadata
                        </button>
                        <button onClick={() => exportNfo(focus)}>
                          <ArrowDownToLine size={14} /> Export NFO
                        </button>
                        {focus.copy.role === "source" && (
                          <button
                            className="primary"
                            onClick={() => {
                              setArea("Organize");
                              setTargetPath("");
                            }}
                          >
                            Organize copy <ArrowRight size={14} />
                          </button>
                        )}
                      </div>
                      <div className="integrity">
                        <ShieldCheck size={17} />
                        <span>
                          {focus.copy.role === "source"
                            ? `Review eligible after ${settings?.preferences.seedRetentionDays} days observed. Never automatically deleted.`
                            : "Offline copies remain part of your collection."}
                        </span>
                      </div>
                    </div>
                  </aside>
                </>
              )}
            </div>
          )}
          {area === "Organize" && (
            <>
              <div className="two-panes">
                <section className="pane">
                  <div className="pane-heading">
                    <Inbox size={17} />
                    <strong>Retained sources</strong>
                    <Badge value="read only" />
                  </div>
                  {table(rows, true)}
                </section>
                <section
                  className="pane destination"
                  onDragOver={(e) => e.preventDefault()}
                  onDrop={(e) => {
                    e.preventDefault();
                    const row = rows.find(
                      (r) =>
                        r.copy.id ===
                        Number(
                          e.dataTransfer.getData("application/x-library-copy"),
                        ),
                    );
                    if (row) {
                      setFocus(row);
                      previewCopy(row);
                    }
                  }}
                >
                  <div className="pane-heading">
                    <Folder size={17} />
                    <strong>Organized library</strong>
                    <Badge value="independent copy" />
                  </div>
                  <div className="destination-body">
                    <label>
                      Destination root
                      <select
                        value={targetRoot}
                        onChange={(e) => setTargetRoot(e.target.value)}
                      >
                        {settings?.roots
                          .filter((r) => r.role === "library")
                          .map((r) => (
                            <option key={r.id} value={r.id}>
                              {r.label}
                            </option>
                          ))}
                      </select>
                    </label>
                    <div className="destination-tree">
                      <span>
                        <Folder size={17} /> Library
                      </span>
                      <span>
                        ├ <Folder size={16} /> Movies
                      </span>
                      <span>
                        └ <Folder size={16} /> TV{" "}
                        <span className="muted">/ Season 01</span>
                      </span>
                    </div>
                    <label>
                      Destination path{" "}
                      <span className="muted">optional override</span>
                      <input
                        value={targetPath}
                        onChange={(e) => setTargetPath(e.target.value)}
                        placeholder="Use Plex-compatible naming"
                      />
                    </label>
                    <div className="drop-zone">
                      <ArrowLeftRight size={28} />
                      <h3>
                        {focus
                          ? focus.title.name
                          : "Select a source to organize"}
                      </h3>
                      <p>
                        Drop a row here, or select it and preview.
                        <br />
                        Nothing is copied until you approve the plan.
                      </p>
                      <button
                        className="primary"
                        disabled={!focus || !targetRoot || busy}
                        onClick={() => focus && previewCopy(focus)}
                      >
                        Preview copy <ArrowRight size={15} />
                      </button>
                    </div>
                  </div>
                </section>
              </div>
              <div className="workflow-note">
                <ShieldCheck size={17} />
                <span>
                  Copy → SHA-256 read-back → publish without overwrite. Source
                  files remain unchanged.
                </span>
              </div>
            </>
          )}
          {area === "Discs" && (
            <>
              <div className="disc-intro">
                <Disc3 size={28} />
                <div>
                  <strong>
                    {bytes(settings?.preferences.discCapacityBytes ?? 50e9)}{" "}
                    nominal capacity
                  </strong>
                  <p>
                    {bytes(settings?.preferences.discReserveBytes ?? 1e9)}{" "}
                    reserved · Whole files only · Select verified library copies
                    below
                  </p>
                </div>
                <span className="badge">External burning</span>
              </div>
              {table(rows)}
              <BackupWorkspace selected={chosen} settings={settings} />
              <div className="section-heading">
                <h2>Legacy disc batches</h2>
                <span>{discs.length} batches</span>
              </div>
              <div className="disc-grid">
                {discs.map((d) => (
                  <article className="disc-card" key={d.id}>
                    <Disc3 size={32} />
                    <div className="disc-card-title">
                      <h3>{d.label}</h3>
                      <Badge value={d.status} />
                    </div>
                    <p>
                      {(JSON.parse(d.manifest) as Entry[]).length} files ·{" "}
                      {bytes(
                        (JSON.parse(d.manifest) as Entry[]).reduce(
                          (s, e) => s + e.bytes,
                          0,
                        ),
                      )}
                    </p>
                    <div className="capacity">
                      <span
                        style={{
                          width: `${((JSON.parse(d.manifest) as Entry[]).reduce((s, e) => s + e.bytes, 0) / (d.capacityBytes - d.reserveBytes)) * 100}%`,
                        }}
                      />
                    </div>
                    <div className="disc-actions">
                      <a
                        className="button"
                        href={`/api/discs/${d.id}/manifest`}
                      >
                        Manifest <ArrowDownToLine size={13} />
                      </a>
                      <button
                        disabled={
                          busy ||
                          !settings?.roots.some((r) => r.role === "staging")
                        }
                        onClick={() =>
                          run(async () => {
                            await api(`/discs/${d.id}/stage`, {
                              rootId: settings!.roots.find(
                                (r) => r.role === "staging",
                              )!.id,
                            });
                            setNotice(
                              "Disc staging queued. Burn the contents of the batch folder in BurnAware Free.",
                            );
                          })
                        }
                      >
                        Stage files
                      </button>
                      {d.status === "planned" ? (
                        <button
                          onClick={() =>
                            run(async () => {
                              await api(`/discs/${d.id}/written`, {});
                              setNotice(
                                "Disc reported written. Read-back verification is still required.",
                              );
                            })
                          }
                        >
                          Report written
                        </button>
                      ) : (
                        <button
                          disabled={
                            !settings?.roots.some((r) => r.role === "disc")
                          }
                          onClick={() =>
                            run(async () => {
                              await api(`/discs/${d.id}/verify`, {
                                rootId: settings!.roots.find(
                                  (r) => r.role === "disc",
                                )!.id,
                              });
                              setNotice(
                                "Folder content check queued. This cannot establish optical verification.",
                              );
                            })
                          }
                        >
                          Check legacy content
                        </button>
                      )}
                    </div>
                  </article>
                ))}
              </div>
              {!discs.length && (
                <div className="empty small">
                  <Disc3 size={32} />
                  <h3>Your archive starts here</h3>
                  <p>
                    Select verified copies above to preview your first disc
                    batch.
                  </p>
                </div>
              )}
            </>
          )}
          {["Organize", "Discs"].includes(area) && (
            <div className="table-footer">
              <span>
                {total
                  ? `${page * 50 + 1}–${Math.min((page + 1) * 50, total)} of ${total} copies`
                  : "0 copies"}
              </span>
              <div>
                <button
                  aria-label="Previous page"
                  disabled={page === 0}
                  onClick={() => {
                    setPage((p) => p - 1);
                    setSelected(new Set());
                  }}
                >
                  <ChevronLeft size={16} />
                </button>
                <button
                  aria-label="Next page"
                  disabled={(page + 1) * 50 >= total}
                  onClick={() => {
                    setPage((p) => p + 1);
                    setSelected(new Set());
                  }}
                >
                  <ChevronRight size={16} />
                </button>
              </div>
            </div>
          )}
          {area === "Jobs" && (
            <section className="jobs-list">
              <div className="pane-heading">
                <Layers3 size={17} />
                <strong>Operation journal</strong>
                <span className="muted">
                  One active file job · up to 100 queued
                </span>
              </div>
              {jobs.map((job) => (
                <article className="job" key={job.id}>
                  <div className={`job-icon ${job.status}`}>
                    {job.status === "running" ? (
                      <LoaderCircle size={21} className="spin" />
                    ) : job.status === "completed" ? (
                      <CheckCircle2 size={21} />
                    ) : job.type.includes("disc") ? (
                      <Disc3 size={21} />
                    ) : (
                      <FileVideo size={21} />
                    )}
                  </div>
                  <div className="job-main">
                    <strong>
                      {statusLabel(job.type)} <Badge value={job.status} />
                    </strong>
                    <small>
                      {new Date(job.createdUtc).toLocaleString()} ·{" "}
                      {job.id.slice(0, 8)}{" "}
                      {job.progressBytes > 0 &&
                        `· ${bytes(job.progressBytes)} copied`}
                    </small>
                    {job.error && <p className="job-error">{job.error}</p>}
                    <details>
                      <summary>Plan details</summary>
                      <pre>
                        {JSON.stringify(JSON.parse(job.payload), null, 2)}
                      </pre>
                    </details>
                  </div>
                  {["queued", "running"].includes(job.status) && (
                    <button
                      disabled={job.cancelRequested || busy}
                      onClick={() =>
                        run(async () => {
                          await api(`/jobs/${job.id}/cancel`, {});
                        })
                      }
                    >
                      {job.cancelRequested
                        ? "Cancellation requested"
                        : "Cancel"}
                    </button>
                  )}
                  {["failed", "cancelled"].includes(job.status) && (
                    <button
                      disabled={busy}
                      onClick={() =>
                        run(async () => {
                          await api(`/jobs/${job.id}/retry`, {});
                        })
                      }
                    >
                      Retry
                    </button>
                  )}
                </article>
              ))}
              {!jobs.length && (
                <div className="empty">
                  <Layers3 size={32} />
                  <h3>All quiet here</h3>
                  <p>
                    Scans, approved copies and disc operations will appear here.
                  </p>
                </div>
              )}
            </section>
          )}
          {area === "Settings" && settings && (
            <SettingsPanel
              settings={settings}
              busy={busy}
              save={(preferences) =>
                run(async () => {
                  await api("/settings", preferences, "PUT");
                  setNotice(
                    "Preferences saved. Retention expiry only enables review.",
                  );
                })
              }
              scan={(id) =>
                run(async () => {
                  await api("/scan", { rootId: id });
                  setNotice("Read-only scan queued.");
                })
              }
            />
          )}
          <footer className="page-footer">
            <span>
              <ShieldCheck size={13} /> Original files protected
            </span>
            <span>
              Library <span className="muted">/</span> v0.1 · Local-first
            </span>
          </footer>
        </div>
      </main>
      <dialog
        ref={dialogRef}
        onCancel={(e) => {
          e.preventDefault();
          close();
        }}
      >
        <div className="modal-heading">
          <div>
            <span className="eyebrow">REVIEW BEFORE APPLYING</span>
            <h2>
              {copyPreview
                ? "Your copy plan"
                : discPreview
                  ? "Your disc batches"
                  : "Bulk metadata"}
            </h2>
          </div>
          <button
            className="icon-button"
            aria-label="Close preview"
            onClick={close}
          >
            <X size={20} />
          </button>
        </div>
        {error && (
          <div role="alert" className="alert error">
            {error}
          </div>
        )}
        {copyPreview && (
          <>
            {copyPreview.completion && (
              <div className="alert">
                {copyPreview.completion.synthetic ? "SYNTHETIC CHECK · " : ""}
                {copyPreview.completion.message}
              </div>
            )}
            <div className="plan-path">
              <small>SOURCE · RETAINED</small>
              <code>{copyPreview.sourcePath}</code>
              <ArrowDownToLine size={20} />
              <small>INDEPENDENT DESTINATION</small>
              <code>{copyPreview.plan.targetPath}</code>
            </div>
            <dl className="plan-details">
              <dt>Copy size</dt>
              <dd>{bytes(copyPreview.plan.bytes)}</dd>
              <dt>Available at destination</dt>
              <dd>{bytes(copyPreview.freeBytes)}</dd>
              <dt>Verification</dt>
              <dd>SHA-256 source stream + destination read-back</dd>
            </dl>
            <p className="modal-note">
              A new file will be created. Existing files are never overwritten.
              The original and its sidecars remain untouched.
            </p>
            <div className="modal-actions">
              <button onClick={close}>Back</button>
              <button
                className="primary"
                disabled={busy}
                onClick={() =>
                  run(async () => {
                    await api(`/copy/${copyPreview.job.id}/commit`, {});
                    close();
                    setNotice(
                      "Copy approved and queued. Track verification in Jobs.",
                    );
                  })
                }
              >
                Approve & copy <ArrowRight size={15} />
              </button>
            </div>
          </>
        )}
        {metaOpen && (
          <>
            {metaPreview?.provenance && (
              <div className="alert">
                {metaPreview.provenance.synthetic ? "SYNTHETIC MATCH · " : ""}
                {metaPreview.provenance.providerId} /{" "}
                {metaPreview.provenance.candidateId}. Locked fields stay
                unchanged; approval updates catalog metadata only.
              </div>
            )}
            <p className="modal-note">
              {new Set(metadataRows.map((r) => r.title.id)).size} titles
              selected. Metadata changes update the catalog. File paths below
              are naming suggestions; copying requires a separate approved plan.
            </p>
            {!metaPreview ? (
              <>
                <div className="metadata-fields">
                  {fields.map((field) => (
                    <div className="metadata-field" key={field}>
                      <label htmlFor={`mode-${field}`}>
                        {field === "name" ? "Title" : field}
                      </label>
                      <select
                        id={`mode-${field}`}
                        value={edits[field].mode}
                        onChange={(e) =>
                          setEdits((old) => ({
                            ...old,
                            [field]: {
                              ...old[field],
                              mode: e.target.value as Edit["mode"],
                            },
                          }))
                        }
                      >
                        <option value="leave">Leave unchanged</option>
                        <option value="set">Set value</option>
                        <option value="clear">Clear value</option>
                      </select>
                      <input
                        aria-label={`${field} value`}
                        disabled={edits[field].mode !== "set"}
                        value={edits[field].value ?? ""}
                        onChange={(e) =>
                          setEdits((old) => ({
                            ...old,
                            [field]: { ...old[field], value: e.target.value },
                          }))
                        }
                      />
                    </div>
                  ))}
                </div>
                <div className="lock-controls">
                  <LockKeyhole size={16} />
                  <label>
                    <input
                      type="checkbox"
                      checked={locks !== null}
                      onChange={(e) => setLocks(e.target.checked ? [] : null)}
                    />{" "}
                    Replace manual locks
                  </label>
                  {locks !== null &&
                    fields.map((f) => (
                      <label key={f}>
                        <input
                          type="checkbox"
                          checked={locks.includes(f)}
                          onChange={(e) =>
                            setLocks((old) =>
                              e.target.checked
                                ? [...old!, f]
                                : old!.filter((x) => x !== f),
                            )
                          }
                        />
                        {f}
                      </label>
                    ))}
                </div>
                <p className="muted">
                  Existing locks are preserved by default. Unlock a field in a
                  separate change before editing it.
                </p>
                {metadataRows.length === 1 && (
                  <label className="nfo-upload">
                    Import NFO into preview
                    <input
                      aria-label="Import NFO"
                      type="file"
                      accept=".nfo,.xml"
                      onChange={(e) => {
                        const file = e.target.files?.[0];
                        if (file)
                          run(async () => {
                            const xml = await file.text();
                            const change = await api<Change>(
                              "/nfo/import-preview",
                              { titleId: metadataRows[0].title.id, xml },
                            );
                            setEdits(change.fields);
                            setNfoXml(xml);
                          });
                      }}
                    />
                  </label>
                )}
                <div className="modal-actions">
                  <button onClick={close}>Cancel</button>
                  <button
                    className="primary"
                    disabled={busy || !metadataRows.length}
                    onClick={previewMetadata}
                  >
                    Preview changes <ArrowRight size={15} />
                  </button>
                </div>
              </>
            ) : (
              <>
                <div className="metadata-preview">
                  {metaPreview.rows.map((row) => (
                    <div key={row.before.id}>
                      <h3>{row.before.name}</h3>
                      {fields.map((f) => (
                        <div className="change-line" key={f}>
                          <small>{f}</small>
                          <span>
                            {String(
                              row.before[f as keyof typeof row.before] ??
                                "Empty",
                            )}
                          </span>
                          <ArrowRight size={13} />
                          <strong>
                            {String(
                              row.after[f as keyof typeof row.after] ?? "Empty",
                            )}
                          </strong>
                        </div>
                      ))}
                      <div className="change-line">
                        <small>Locks</small>
                        <span>{row.before.lockedFields}</span>
                        <ArrowRight size={13} />
                        <strong>{row.after.lockedFields}</strong>
                      </div>
                      {row.paths.map((path, i) => (
                        <div className="path-change" key={i}>
                          <code>{path.oldPath}</code>
                          <ArrowRight size={12} />
                          <code>{path.newPath}</code>
                        </div>
                      ))}
                    </div>
                  ))}
                </div>
                <div className="modal-actions">
                  <button onClick={() => setMetaPreview(null)}>
                    Back to edit
                  </button>
                  <button
                    className="primary"
                    disabled={busy}
                    onClick={() =>
                      run(async () => {
                        await api(`/metadata/${metaPreview.job.id}/commit`, {});
                        if (nfoXml && metadataRows.length === 1)
                          await exportNfo(metadataRows[0]);
                        close();
                        setNotice(
                          "Catalog metadata updated. No media paths were changed.",
                        );
                      })
                    }
                  >
                    Apply catalog changes
                  </button>
                </div>
              </>
            )}
          </>
        )}
        {discPreview && (
          <>
            <p className="modal-note">
              {bytes(discPreview.plan.capacityBytes)} nominal ·{" "}
              {bytes(discPreview.plan.reserveBytes)} reserve per disc. Seasons
              may span discs; files never do.
            </p>
            <div className="metadata-preview">
              {discPreview.plan.bins.map((bin, i) => (
                <div className="batch-preview" key={i}>
                  <h3>
                    <Disc3 size={18} /> Batch {i + 1}{" "}
                    <span>{bytes(bin.reduce((s, e) => s + e.bytes, 0))}</span>
                  </h3>
                  {bin.map((entry) => (
                    <p key={entry.copyId}>
                      <code>{entry.path}</code>
                      <span>{bytes(entry.bytes)}</span>
                    </p>
                  ))}
                </div>
              ))}
            </div>
            <div className="modal-actions">
              <button onClick={close}>Back</button>
              <button
                className="primary"
                disabled={busy}
                onClick={() =>
                  run(async () => {
                    await api(`/discs/${discPreview.job.id}/commit`, {});
                    close();
                    setNotice(
                      "Disc plan saved. Stage each batch, then burn it using BurnAware Free on Windows.",
                    );
                  })
                }
              >
                Save disc plan
              </button>
            </div>
          </>
        )}
      </dialog>
    </div>
  );
}
function SettingsPanel({
  settings,
  busy,
  save,
  scan,
}: {
  settings: Settings;
  busy: boolean;
  save: (p: Preferences) => void;
  scan: (id: string) => void;
}) {
  const [preferences, setPreferences] = useState(settings.preferences);
  return (
    <div className="settings-grid">
      <section className="settings-card">
        <h2>
          <HardDrive size={19} /> Storage roots
        </h2>
        <p>
          Configured at startup. Windows paths are display mappings; all
          operations use container roots.
        </p>
        {settings.roots.map((root) => (
          <div className="root-card" key={root.id}>
            <div>
              <strong>{root.label}</strong>
              <Badge value={root.online ? "online" : "offline"} />
              <span className="muted">{root.role}</span>
            </div>
            <dl>
              <dt>Windows</dt>
              <dd>
                <code>{root.windowsPath || "Not mapped"}</code>
              </dd>
              <dt>Container</dt>
              <dd>
                <code>{root.containerPath}</code>
              </dd>
            </dl>
            {["source", "library"].includes(root.role) && (
              <button
                disabled={!root.online || busy}
                onClick={() => scan(root.id)}
              >
                <Download size={14} /> Scan read-only
              </button>
            )}
          </div>
        ))}
        {!settings.roots.length && (
          <div className="empty small">
            <Folder size={28} />
            <h3>No roots configured</h3>
            <p>
              Add explicit root mappings in your local configuration and restart
              the service. See the repository’s Docker Desktop setup guide.
            </p>
          </div>
        )}
      </section>
      <div>
        <section className="settings-card">
          <h2>
            <ShieldCheck size={19} /> Preservation preferences
          </h2>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              save(preferences);
            }}
          >
            <label>
              Seed retention review (days)
              <input
                type="number"
                min="0"
                max="3650"
                required
                value={preferences.seedRetentionDays}
                onChange={(e) =>
                  setPreferences((p) => ({
                    ...p,
                    seedRetentionDays: Number(e.target.value),
                  }))
                }
              />
            </label>
            <p>
              Measured from first observed source copy. This is review
              eligibility, not a torrent completion date or deletion schedule.
            </p>
            <label>
              Disc capacity (decimal bytes)
              <input
                type="number"
                min="1000001"
                required
                value={preferences.discCapacityBytes}
                onChange={(e) =>
                  setPreferences((p) => ({
                    ...p,
                    discCapacityBytes: Number(e.target.value),
                  }))
                }
              />
            </label>
            <label>
              Capacity reserve (bytes)
              <input
                type="number"
                min="1000000"
                required
                value={preferences.discReserveBytes}
                onChange={(e) =>
                  setPreferences((p) => ({
                    ...p,
                    discReserveBytes: Number(e.target.value),
                  }))
                }
              />
            </label>
            <p>
              50 GB = 50,000,000,000 bytes ≈ 46.57 GiB. Reserve covers
              filesystem and manifest overhead.
            </p>
            <button className="primary" disabled={busy}>
              Save preferences
            </button>
          </form>
        </section>
        <section className="settings-card">
          <h2>
            <Archive size={19} /> Keep your catalog portable
          </h2>
          <p>
            Download a consistent SQLite backup or a JSON export including the
            catalog and operation history. Media files are separate.
          </p>
          <div className="backup-actions">
            <a className="button" href="/api/backup">
              <Download size={14} /> SQLite backup
            </a>
            <a className="button" href="/api/export">
              JSON export
            </a>
          </div>
        </section>
      </div>
      <CompanionSettings />
      <PreservationSettings />
      <IntegrationSettings roots={settings.roots} />
    </div>
  );
}
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
