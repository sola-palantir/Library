import { useEffect, useMemo, useState } from "react";
import { api } from "./types";
import type { Job, Root, Settings } from "./types";
import type { TrackReview } from "./mediaPolicy";

type Proposal = {
  job: Job;
  targetPath: string;
  executionAvailable: boolean;
  warning: string;
  plan: {
    streams: { index: number; type: string; codec: string; language: string }[];
    encoding?: {
      id: string;
      presetSha256: string;
      crf: number;
      preset: string;
      pixelFormat: string;
    } | null;
  };
};
export function SampleRemuxReview({
  copyId,
  review,
  originals,
}: {
  copyId: number | null;
  review: TrackReview | null;
  originals: string;
}) {
  const [roots, setRoots] = useState<Root[]>([]);
  const [target, setTarget] = useState("");
  const [seconds, setSeconds] = useState(5);
  const [mode, setMode] = useState("stream-copy");
  const [complete, setComplete] = useState(false);
  const [sdr, setSdr] = useState(false);
  const [proposal, setProposal] = useState<Proposal | null>(null);
  const [job, setJob] = useState<Job | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const request = useMemo(
    () => ({
      sourceCopyId: copyId,
      targetRootId: target,
      seconds,
      mode,
      originalLanguages: originals
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean),
      confirmSourceComplete: complete,
      confirmSdr: sdr,
      tracks:
        review?.decisions
          .filter((s) => s.type === "audio" || s.type === "subtitle")
          .map((s) => ({
            index: s.index,
            language: s.language,
            role: s.type === "audio" ? s.role : "subtitle",
          })) ?? [],
    }),
    [copyId, target, seconds, mode, originals, complete, sdr, review],
  );
  const signature = JSON.stringify(request);
  useEffect(() => {
    setProposal(null);
    setJob(null);
    setError("");
  }, [signature]);
  useEffect(() => {
    setComplete(false);
    setSdr(false);
    if (copyId === null) return;
    let active = true;
    api<Settings>("/settings")
      .then((s) => {
        if (active)
          setRoots(s.roots.filter((r) => r.role === "staging" && r.online));
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [copyId]);
  useEffect(() => {
    if (!job || !["queued", "running"].includes(job.status)) return;
    let active = true;
    const id = setInterval(() => {
      api<Job[]>("/jobs")
        .then((jobs) => {
          if (active) setJob(jobs.find((j) => j.id === job.id) ?? job);
        })
        .catch((e) => {
          if (active) setError(e.message);
        });
    }, 1500);
    return () => {
      active = false;
      clearInterval(id);
    };
  }, [job]);
  async function act(action: "preview" | "approve" | "withdraw" | "cancel") {
    setBusy(true);
    setError("");
    try {
      if (action === "preview")
        setProposal(await api<Proposal>("/media/remux/preview", request));
      else if (proposal) {
        if (action === "cancel")
          await api(`/jobs/${proposal.job.id}/cancel`, {});
        else await api(`/media/remux/${proposal.job.id}/${action}`, {});
        setJob(
          (await api<Job[]>("/jobs")).find((j) => j.id === proposal.job.id) ??
            null,
        );
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  if (copyId === null)
    return (
      <p>
        Processing previews require a fresh inspection of a catalog file.
        Imported and synthetic metadata cannot authorize a media operation.
      </p>
    );
  return (
    <section className="media-card">
      <h2>Stage a media sample</h2>
      <p>
        Preview up to 30 seconds from the beginning. Keep the original untouched
        and create one separate sample in staging. Samples omit chapters and
        cannot be archived as complete titles. HDR, Dolby Vision and HEVC inputs
        remain blocked. Audio and retained subtitles always use stream copy.
      </p>
      <fieldset disabled={busy || !!job}>
        <div className="media-controls">
          <label>
            Sample mode
            <select value={mode} onChange={(e) => setMode(e.target.value)}>
              <option value="stream-copy">
                Stream copy — no video compression
              </option>
              <option value="x265-sdr-sample">
                SDR x265 — CRF 22, Slow, 8-bit
              </option>
            </select>
          </label>
          <label>
            Sample staging destination
            <select value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="">Choose isolated staging</option>
              {roots.map((r) => (
                <option value={r.id} key={r.id}>
                  {r.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            Sample seconds
            <input
              type="number"
              min={1}
              max={30}
              value={seconds}
              onChange={(e) => setSeconds(Number(e.target.value))}
            />
          </label>
        </div>
        <label>
          <input
            type="checkbox"
            checked={complete}
            onChange={(e) => setComplete(e.target.checked)}
          />
          I confirm this source is complete and no longer being copied.
        </label>
        <label>
          <input
            type="checkbox"
            checked={sdr}
            onChange={(e) => setSdr(e.target.checked)}
          />
          I reviewed this as SDR H.264 8-bit. This does not override HDR
          refusal.
        </label>
        <button
          disabled={
            !target || !complete || !sdr || review?.status !== "preview-ready"
          }
          onClick={() => void act("preview")}
        >
          Preview sample plan
        </button>
      </fieldset>
      {error && (
        <p role="alert" className="media-error">
          {error}
        </p>
      )}
      {proposal && (
        <div>
          <h3>Review the sample plan</h3>
          <p>{proposal.warning}</p>
          <p>
            Destination: <code>{proposal.targetPath}</code>
          </p>
          <p>
            Selected source streams:{" "}
            {proposal.plan.streams
              .map((s) => `#${s.index} ${s.type} ${s.codec} ${s.language}`)
              .join("; ")}
            .
          </p>
          {proposal.plan.encoding ? (
            <p>
              Video: x265 CRF {proposal.plan.encoding.crf},{" "}
              {proposal.plan.encoding.preset},{" "}
              {proposal.plan.encoding.pixelFormat}. Source dimensions and frame
              timing are preserved, without saved crop values. Audio and
              subtitles use copy mode. This is a calibration sample; visual
              quality and whole-title savings have not been established. Preset
              SHA-256: <code>{proposal.plan.encoding.presetSha256}</code>.
            </p>
          ) : (
            <p>Every retained stream uses copy mode.</p>
          )}
          <p>
            Validation must pass before publication; no overwrite fallback is
            allowed.
          </p>
          {!proposal.executionAvailable && (
            <p>
              Execution is disabled in this deployment. An operator must qualify
              storage and explicitly enable the selected sample mode.
            </p>
          )}
          {!job && (
            <button
              disabled={busy || !proposal.executionAvailable}
              onClick={() => void act("approve")}
            >
              Create validated sample
            </button>
          )}
          {job && (
            <>
              <p role="status">
                Sample job: {job.status}. {job.error}
              </p>
              <p>
                Job ID: <code>{job.id}</code>
              </p>
              {["queued", "running"].includes(job.status) ? (
                <button disabled={busy} onClick={() => void act("cancel")}>
                  Cancel sample
                </button>
              ) : (
                job.status !== "withdrawn" && (
                  <button disabled={busy} onClick={() => void act("withdraw")}>
                    Withdraw sample
                  </button>
                )
              )}
              <p>
                Withdrawal removes only an owned incomplete file or marks a
                completed sample withdrawn. Published files and originals are
                retained.
              </p>
            </>
          )}
        </div>
      )}
    </section>
  );
}
