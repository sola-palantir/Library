import type { Job } from "./types";
import { bytes } from "./types";
import "./motion.css";

const phases: Record<string, string> = {
  starting: "Working · no measured total available",
  scanning: "Scanning · total file count unknown",
  copying: "Copying independent destination",
  "checking-recovery": "Checking recovered destination",
  "verifying-staged-copy": "Verifying temporary copy · SHA-256",
  "verifying-published-copy": "Verifying published copy · SHA-256",
  "staging-file": "Copying current staging file",
  "verifying-staged-file": "Verifying current staging file · SHA-256",
  "verifying-batch-file": "Final batch check · current file",
  "auditing-file": "Auditing file · SHA-256",
};
export function JobProgress({ job, stale }: { job: Job; stale: boolean }) {
  let progress: {
    phase?: string;
    bytes?: number;
    totalBytes?: number | null;
    files?: number | null;
    atUtc?: string;
  } = {};
  try {
    const parsed = JSON.parse(job.progressJson || "{}");
    if (parsed && typeof parsed === "object") progress = parsed;
  } catch {
    /* Older jobs have no phase evidence. */
  }
  const active = ["running", "auditing"].includes(job.status);
  const queued = job.status === "queued";
  const terminal = !active && !queued;
  const phase =
    phases[progress.phase ?? ""] ?? "Working · no measured total available";
  const measured =
    Number.isFinite(progress.bytes) &&
    Number.isFinite(progress.totalBytes) &&
    (progress.totalBytes ?? 0) > 0;
  const percent = measured
    ? Math.min(100, Math.max(0, (progress.bytes! / progress.totalBytes!) * 100))
    : undefined;
  const label = queued
    ? "Waiting for worker · progress resets when work resumes"
    : job.status === "draft"
      ? "Awaiting approval"
      : terminal
        ? `${job.status.replaceAll("-", " ")} · ${progress.phase && progress.phase !== "starting" ? phase : "Journal result"}`
        : phase;
  return (
    <div
      className={`job-progress ${active && !stale && !job.cancelRequested ? "is-active" : ""} ${job.status === "completed" ? "is-complete" : ""}`}
    >
      <div
        className="progress-heading"
        role="status"
        aria-live="polite"
        aria-atomic="true"
      >
        {job.cancelRequested && active
          ? "Cancellation requested · waiting for a safe checkpoint"
          : label}
      </div>
      {active && (
        <div
          className={`progress-track ${measured ? "determinate" : "indeterminate"}`}
          role="progressbar"
          aria-label={phase}
          aria-valuemin={measured ? 0 : undefined}
          aria-valuemax={measured ? 100 : undefined}
          aria-valuenow={
            percent === undefined ? undefined : Math.floor(percent)
          }
          aria-valuetext={
            measured
              ? `${bytes(progress.bytes!)} of ${bytes(progress.totalBytes!)} in this phase${stale ? "; updates unavailable" : ""}`
              : "Total unknown"
          }
        >
          <span style={measured ? { width: `${percent}%` } : undefined} />
        </div>
      )}
      {!queued && progress.phase && progress.phase !== "starting" && (
        <div className="progress-detail">
          <span>
            {measured
              ? `${bytes(progress.bytes!)} / ${bytes(progress.totalBytes!)} · this phase`
              : "No percentage estimate"}
            {progress.files != null &&
              ` · ${progress.files.toLocaleString()} ${progress.phase === "scanning" ? "files examined" : "files checked"}`}
          </span>
          {progress.atUtc && (
            <time dateTime={progress.atUtc}>
              Last measured {new Date(progress.atUtc).toLocaleTimeString()}
            </time>
          )}
        </div>
      )}
      {active && measured && percent === 100 && (
        <small>Phase bytes processed; waiting for the journal result.</small>
      )}
    </div>
  );
}
