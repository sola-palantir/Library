import { useEffect, useRef, useState } from "react";
import { api } from "./types";
import { WorkerConnection, type Connection } from "./WorkerConnection";

type Session = {
  enabled: boolean;
  connected: boolean;
  folder: string | null;
  expiresUtc: string | null;
  maxFiles: number;
  connection?: Connection;
};
type Scan = { id: string; status: string; error: string | null };

export function SampleFolder({ refresh }: { refresh: () => void }) {
  const [session, setSession] = useState<Session | null>(null);
  const [folder, setFolder] = useState("");
  const [jobs, setJobs] = useState<Scan[]>([]);
  const [error, setError] = useState("");
  const [statusError, setStatusError] = useState("");
  const [busy, setBusy] = useState(false);
  const [revision, setRevision] = useState(0);
  const completed = useRef(new Set<string>());
  const refreshRef = useRef(refresh);
  refreshRef.current = refresh;
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const [state, scans] = await Promise.all([
          api<Session>("/review-scan"),
          api<Scan[]>("/review-scan/jobs"),
        ]);
        if (stopped) return;
        if (
          !state ||
          typeof state.enabled !== "boolean" ||
          !Array.isArray(scans) ||
          scans.some(
            (scan) =>
              !scan ||
              typeof scan.id !== "string" ||
              typeof scan.status !== "string",
          )
        ) {
          throw new Error(
            "The scan service returned an invalid status response. Refresh or retry when it is available.",
          );
        }
        setSession(state);
        setStatusError("");
        setFolder((previous) => previous || state.folder || "");
        setJobs(scans);
        let changed = false;
        for (const scan of scans)
          if (scan.status === "completed" && !completed.current.has(scan.id)) {
            completed.current.add(scan.id);
            changed = true;
          }
        if (changed) refreshRef.current();
      } catch (e) {
        if (!stopped) {
          setStatusError((e as Error).message);
          setSession((old) =>
            old
              ? {
                  ...old,
                  enabled: false,
                  connected: false,
                  connection: old.connection
                    ? {
                        ...old.connection,
                        state: "unavailable",
                        canConnect: false,
                      }
                    : undefined,
                }
              : null,
          );
        }
      } finally {
        if (!stopped) timer = setTimeout(poll, 1000);
      }
    }
    void poll();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [revision]);
  const active = jobs.find((job) => ["queued", "running"].includes(job.status));
  async function start(retryId?: string) {
    setBusy(true);
    setError("");
    try {
      const job = await api<Scan>(
        retryId ? `/review-scan/jobs/${retryId}/retry` : "/review-scan",
        retryId ? {} : { folder },
      );
      setJobs((old) => [job, ...old.filter((j) => j.id !== job.id)]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function cancel() {
    if (!active) return;
    setBusy(true);
    setError("");
    try {
      await api(`/review-scan/jobs/${active.id}/cancel`, {});
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="media-card">
      <h2>Add sample folder</h2>
      {session?.connection && (
        <WorkerConnection
          connection={session.connection}
          folder={folder}
          changed={() => setRevision((v) => v + 1)}
        />
      )}
      <p>
        Inspect up to {session?.maxFiles ?? 8} top-level sample files. Files
        remain in place; no encoding, copying or subtitle changes occur.
      </p>
      <div className="media-controls">
        <label>
          Windows sample folder
          <input
            value={folder}
            onChange={(e) => setFolder(e.target.value)}
            disabled={!!active || busy || !!session?.connection?.configured}
            placeholder="Approved local sample folder"
          />
        </label>
        <button
          onClick={() => void start()}
          disabled={
            busy ||
            !!active ||
            !session?.enabled ||
            !session?.connected ||
            !folder
          }
        >
          Add folder and scan read-only
        </button>
        {active && (
          <button onClick={cancel} disabled={busy}>
            Cancel scan
          </button>
        )}
      </div>
      <p role="status">
        {session?.connection?.configured
          ? "Use the Windows worker connection controls above. Encoding and file-changing operations remain disabled."
          : !session?.enabled
            ? "No active read-only worker session. Start a temporary session for this folder; persistent pairing and encoding remain off."
            : !session.connected
              ? "Waiting for the Windows read-only worker. No scan will start while disconnected."
              : `Windows read-only worker connected until ${new Date(session.expiresUtc!).toLocaleTimeString()}. Only the approved folder is accessible.`}
      </p>
      {error && (
        <p role="alert" className="media-error">
          {error}
        </p>
      )}
      {statusError && (
        <p role="alert" className="media-error">
          {statusError} Connection status is unavailable; scanning is disabled
          until status recovers.
        </p>
      )}
      {jobs.slice(0, 5).map((job) => (
        <div key={job.id}>
          <p>
            Sample scan: <strong>{job.status}</strong>
            {job.status === "completed" &&
              " — select a catalog file below and choose Inspect read-only."}
            {job.error && ` — ${job.error}`}
          </p>
          {["failed", "cancelled"].includes(job.status) && (
            <button
              onClick={() => void start(job.id)}
              disabled={
                busy || !!active || !session?.connected || !session.enabled
              }
            >
              Retry scan
            </button>
          )}
        </div>
      ))}
      <p>
        Each scan has a three-minute deadline; each FFprobe process has a
        45-second limit. A failed or cancelled scan registers no partial
        inventory. Reviews are observations, not content verification or
        playback qualification.
      </p>
    </section>
  );
}
