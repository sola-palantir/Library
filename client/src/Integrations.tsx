import { useEffect, useState } from "react";
import { api, bytes } from "./types";
import type { MetaPreview, Row, Root } from "./types";

type Mapping = { remotePrefix: string; rootId: string; relativePrefix: string };
type Configuration = {
  mode: string;
  baseUrl: string;
  category: string;
  mappings: Mapping[];
  revision: number;
};
type TorrentFile = {
  name: string;
  bytes: number;
  progress: number;
  torrentState: string;
  rootId: string | null;
  relativePath: string | null;
  readiness: string;
  reason: string;
  seedingSeconds: number | null;
};
type Snapshot = {
  state: string;
  message: string;
  applicationVersion: string | null;
  apiVersion: string | null;
  observedUtc: string | null;
  files: TorrentFile[];
  synthetic: boolean;
};
type IntegrationStatus = {
  configuration: Configuration;
  credentialsAvailable: boolean;
  snapshot: Snapshot;
  plex: { state: string; message: string };
};
type Provider = {
  id: string;
  label: string;
  state: string;
  message: string;
  requiresApiKey: boolean;
};
type Candidate = {
  id: string;
  providerId: string;
  name: string;
  kind: string;
  year: number | null;
  plot: string | null;
  genre: string | null;
  synthetic: boolean;
};

export function QBitPanel({
  roots,
  configure = false,
}: {
  roots?: Root[];
  configure?: boolean;
}) {
  const [status, setStatus] = useState<IntegrationStatus | null>(null),
    [draft, setDraft] = useState<Configuration | null>(null),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [busy, setBusy] = useState(false);
  useEffect(() => {
    api<IntegrationStatus>("/integrations/qbit")
      .then((s) => {
        setStatus(s);
        setDraft(s.configuration);
      })
      .catch((e) => setError(e.message));
  }, []);
  async function save() {
    if (!draft) return;
    setBusy(true);
    setError("");
    try {
      const result = await api<IntegrationStatus>(
        "/integrations/qbit",
        draft,
        "PUT",
      );
      setStatus(result);
      setDraft(result.configuration);
      setNotice("Configuration saved. No connection was made.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function refresh() {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const snapshot = await api<Snapshot>("/integrations/qbit/refresh", {});
      setStatus((old) => (old ? { ...old, snapshot } : null));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  if (!status || !draft)
    return (
      <section className="settings-card">
        <h2>qBittorrent · read only</h2>
        <p>{error || "Loading local integration settings…"}</p>
      </section>
    );
  const canRefresh =
    status.configuration.mode !== "disabled" &&
    (status.configuration.mode === "synthetic" || status.credentialsAvailable);
  return (
    <section className="settings-card integration-panel">
      <div className="integration-heading">
        <h2>qBittorrent · read only</h2>
        <span className="badge">{status.snapshot.state}</span>
      </div>
      <p>{status.snapshot.message}</p>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      {notice && (
        <div className="alert" role="status">
          {notice}
        </div>
      )}
      {configure && (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            save();
          }}
        >
          <div className="integration-form">
            <label>
              qBittorrent mode
              <select
                aria-label="qBittorrent mode"
                value={draft.mode}
                onChange={(e) =>
                  setDraft((old) =>
                    old
                      ? {
                          ...old,
                          mode: e.target.value,
                          mappings:
                            e.target.value === "synthetic" &&
                            !old.mappings.length
                              ? [
                                  {
                                    remotePrefix: "X:/LibraryFixture/source",
                                    rootId:
                                      roots?.find((r) => r.role === "source")
                                        ?.id || "",
                                    relativePrefix: "",
                                  },
                                ]
                              : old.mappings,
                        }
                      : old,
                  )
                }
              >
                <option value="disabled">Disabled · manual workflow</option>
                <option value="synthetic">
                  Synthetic demonstration · no network
                </option>
                <option value="live">
                  Live read-only adapter · credentials required
                </option>
              </select>
            </label>
            <label>
              WebUI base URL
              <input
                aria-label="qBittorrent base URL"
                type="url"
                placeholder="http://host.docker.internal:8080/"
                required={draft.mode === "live"}
                disabled={draft.mode !== "live"}
                value={draft.baseUrl}
                onChange={(e) =>
                  setDraft({ ...draft, baseUrl: e.target.value })
                }
              />
            </label>
            <label>
              Category filter (optional)
              <input
                aria-label="qBittorrent category"
                value={draft.category}
                onChange={(e) =>
                  setDraft({ ...draft, category: e.target.value })
                }
              />
            </label>
          </div>
          <p>
            Credentials are never entered or stored here. A future live
            connection uses externally supplied LIBRARY_QBIT_USERNAME and
            LIBRARY_QBIT_PASSWORD.{" "}
            {status.credentialsAvailable
              ? "Credential variables are present; values are not exposed."
              : "Credentials are not configured."}
          </p>
          <h3>Explicit path mappings</h3>
          <p>
            Map the path reported by qBittorrent to a read-only source root.
            This does not create or move files.
          </p>
          {draft.mappings.map((mapping, i) => (
            <div className="mapping-row" key={i}>
              <label>
                qBittorrent path prefix
                <input
                  aria-label={`Remote path ${i + 1}`}
                  value={mapping.remotePrefix}
                  onChange={(e) =>
                    setDraft({
                      ...draft,
                      mappings: draft.mappings.map((m, j) =>
                        i === j ? { ...m, remotePrefix: e.target.value } : m,
                      ),
                    })
                  }
                />
              </label>
              <label>
                Source root
                <select
                  aria-label={`Source root ${i + 1}`}
                  value={mapping.rootId}
                  onChange={(e) =>
                    setDraft({
                      ...draft,
                      mappings: draft.mappings.map((m, j) =>
                        i === j ? { ...m, rootId: e.target.value } : m,
                      ),
                    })
                  }
                >
                  {roots
                    ?.filter((r) => r.role === "source")
                    .map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.label}
                      </option>
                    ))}
                </select>
              </label>
              <label>
                Relative subfolder
                <input
                  aria-label={`Relative prefix ${i + 1}`}
                  placeholder="Optional"
                  value={mapping.relativePrefix}
                  onChange={(e) =>
                    setDraft({
                      ...draft,
                      mappings: draft.mappings.map((m, j) =>
                        i === j ? { ...m, relativePrefix: e.target.value } : m,
                      ),
                    })
                  }
                />
              </label>
              <button
                type="button"
                aria-label={`Remove mapping ${i + 1}`}
                onClick={() =>
                  setDraft({
                    ...draft,
                    mappings: draft.mappings.filter((_, j) => j !== i),
                  })
                }
              >
                Remove
              </button>
            </div>
          ))}
          <div className="integration-actions">
            <button
              type="button"
              onClick={() =>
                setDraft({
                  ...draft,
                  mappings: [
                    ...draft.mappings,
                    {
                      remotePrefix: "",
                      rootId: roots?.find((r) => r.role === "source")?.id || "",
                      relativePrefix: "",
                    },
                  ],
                })
              }
            >
              Add path mapping
            </button>
            <button className="primary" disabled={busy} type="submit">
              Save qBittorrent configuration
            </button>
          </div>
        </form>
      )}
      <div className="integration-actions">
        <button disabled={busy || !canRefresh} onClick={refresh}>
          {busy ? "Checking…" : "Refresh torrent status"}
        </button>
        {status.snapshot.observedUtc && (
          <span className="muted">
            Observed {new Date(status.snapshot.observedUtc).toLocaleString()} ·
            app {status.snapshot.applicationVersion ?? "unknown"} · API{" "}
            {status.snapshot.apiVersion ?? "unknown"}
          </span>
        )}
      </div>
      {status.snapshot.synthetic && (
        <p className="synthetic-note">
          SYNTHETIC DATA · Invented completion responses. No qBittorrent server
          was contacted.
        </p>
      )}
      {status.snapshot.files.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>File</th>
                <th>Completion</th>
                <th>Torrent state</th>
                <th>Mapped source</th>
              </tr>
            </thead>
            <tbody>
              {status.snapshot.files.slice(0, 100).map((file, i) => (
                <tr key={i}>
                  <td>
                    <strong>{file.name}</strong>
                    <small className="integration-detail">
                      {bytes(file.bytes)} · {file.reason}
                    </small>
                  </td>
                  <td>
                    <span className={`badge ${file.readiness}`}>
                      {file.readiness} · {(file.progress * 100).toFixed(0)}%
                    </span>
                  </td>
                  <td>
                    {file.torrentState}
                    {file.seedingSeconds !== null && (
                      <small className="integration-detail">
                        {Math.floor(file.seedingSeconds / 86400)} reported seed
                        days
                      </small>
                    )}
                  </td>
                  <td>
                    <code>
                      {file.rootId
                        ? `${file.rootId}/${file.relativePath}`
                        : "Unmapped"}
                    </code>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {status.snapshot.files.length > 100 && (
            <p>
              Showing 100 of {status.snapshot.files.length} observed files. Copy
              checks use the full bounded snapshot.
            </p>
          )}
        </div>
      )}
      <p className="muted">
        This adapter never adds, removes, pauses, resumes, renames or rechecks
        torrents. Mapped file completion is freshly checked before copy;
        retention review still uses first observation and never triggers
        deletion.
      </p>
    </section>
  );
}
export function IntegrationSettings({ roots }: { roots: Root[] }) {
  return (
    <div className="integration-settings">
      <QBitPanel roots={roots} configure />
      <section className="settings-card">
        <h2>Metadata providers</h2>
        <p>
          Manual editing and NFO import work without an account. The inspector’s
          matching tool uses explicitly labelled synthetic responses. TMDB live
          lookup is unconfigured: a real adapter and a user-supplied API
          credential are still required.
        </p>
      </section>
      <section className="settings-card">
        <h2>Plex · optional integration deferred</h2>
        <p>
          Plex naming is implemented. Server status, section discovery and
          library refresh are not connected or implemented. No Plex credentials
          are requested here. See docs/integrations.md for the proposed boundary
          and setup requirements.
        </p>
      </section>
    </div>
  );
}
export function MetadataMatcher({
  row,
  onPreview,
}: {
  row: Row;
  onPreview: (preview: MetaPreview) => void;
}) {
  const [providers, setProviders] = useState<Provider[]>([]),
    [provider, setProvider] = useState("synthetic"),
    [query, setQuery] = useState(row.title.name),
    [candidates, setCandidates] = useState<Candidate[] | null>(null),
    [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  useEffect(() => {
    api<Provider[]>("/metadata/providers")
      .then(setProviders)
      .catch((e) => setError(e.message));
  }, []);
  useEffect(() => {
    setQuery(row.title.name);
    setCandidates(null);
    setError("");
  }, [row.title.id]);
  async function search() {
    setBusy(true);
    setError("");
    try {
      const result = await api<{ candidates: Candidate[] }>(
        "/metadata/search",
        { titleId: row.title.id, providerId: provider, query },
      );
      setCandidates(result.candidates);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function match(candidate: Candidate) {
    setBusy(true);
    setError("");
    try {
      onPreview(
        await api<MetaPreview>("/metadata/match/preview", {
          titleId: row.title.id,
          revision: row.title.revision,
          providerId: candidate.providerId,
          candidateId: candidate.id,
        }),
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <details className="metadata-matcher">
      <summary>Find a metadata match</summary>
      <p>
        Synthetic responses only. Nothing changes until a match is reviewed and
        approved. Manual editing remains available.
      </p>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      <label>
        Metadata provider
        <select
          aria-label="Metadata provider"
          value={provider}
          onChange={(e) => {
            setProvider(e.target.value);
            setCandidates(null);
          }}
        >
          {providers.map((p) => (
            <option
              key={p.id}
              value={p.id}
              disabled={p.state === "unconfigured"}
            >
              {p.label} · {p.state}
            </option>
          ))}
        </select>
      </label>
      <label>
        Match title
        <input
          aria-label="Match title"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          maxLength={200}
        />
      </label>
      <button disabled={busy || !query.trim()} onClick={search}>
        Search synthetic metadata
      </button>
      {candidates?.length === 0 && (
        <p>
          No synthetic matches. Keep this title unmatched, edit manually, or
          import an NFO.
        </p>
      )}
      {candidates?.map((candidate) => (
        <article className="candidate" key={candidate.id}>
          <strong>
            {candidate.name} ({candidate.year ?? "Unknown year"})
          </strong>
          <span className="badge">Synthetic</span>
          <p>{candidate.plot}</p>
          <button disabled={busy} onClick={() => match(candidate)}>
            Review match · {candidate.year ?? "Unknown year"}
          </button>
        </article>
      ))}
    </details>
  );
}
