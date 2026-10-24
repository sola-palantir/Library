import { useMemo, useState } from "react";
import { api, bytes } from "./types";
import type { Row } from "./types";
import {
  inspectProbe,
  parseMediaJson,
  previewPresets,
  reviewTracks,
} from "./mediaPolicy";
import type {
  Inspection,
  PresetPreview,
  TrackOverride,
  TrackRole,
} from "./mediaPolicy";
import { syntheticProbe } from "./mediaFixture";
import "./mediaReview.css";
import { SampleRemuxReview } from "./SampleRemuxReview";

export function MediaReview({ rows }: { rows: Row[] }) {
  const [inspection, setInspection] = useState<Inspection | null>(null);
  const [origin, setOrigin] = useState("");
  const [liveCopyId, setLiveCopyId] = useState<number | null>(null);
  const [presets, setPresets] = useState<PresetPreview[]>([]);
  const [originals, setOriginals] = useState("");
  const [overrides, setOverrides] = useState<Record<number, TrackOverride>>({});
  const [copyId, setCopyId] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const eligible = rows.filter((r) =>
    ["source", "library"].includes(r.copy.role),
  );
  const result = useMemo(() => {
    if (!inspection) return null;
    try {
      return {
        review: reviewTracks(
          inspection,
          originals
            .split(",")
            .map((v) => v.trim())
            .filter(Boolean),
          overrides,
        ),
        error: "",
      };
    } catch (e) {
      return { review: null, error: (e as Error).message };
    }
  }, [inspection, originals, overrides]);
  function acceptProbe(
    value: unknown,
    label: string,
    catalogCopy: number | null = null,
  ) {
    const parsed = inspectProbe(value);
    setInspection(parsed);
    setOrigin(label);
    setLiveCopyId(catalogCopy);
    setOverrides({});
    setOriginals("");
    setError("");
  }
  async function importFile(file: File | undefined, kind: "probe" | "preset") {
    if (!file) return;
    setError("");
    try {
      if (file.size > 4 * 1024 * 1024)
        throw new Error("Choose a JSON file no larger than 4 MiB.");
      const value = parseMediaJson(await file.text());
      if (kind === "probe")
        acceptProbe(
          value,
          `Imported metadata: ${file.name}. Unverified; not attached to a catalog file.`,
        );
      else setPresets(previewPresets(value));
    } catch (e) {
      setError((e as Error).message);
    }
  }
  async function inspect() {
    if (!eligible.some((r) => String(r.copy.id) === copyId)) return;
    setBusy(true);
    setError("");
    try {
      const result = await api<{
        probe: unknown;
        evidence: { toolVersion: string; observedUtc: string; copyId: number };
      }>(`/media/inspect/${copyId}`, {});
      acceptProbe(
        result.probe,
        `Read-only observation of copy ${result.evidence.copyId} at ${result.evidence.observedUtc}. ${result.evidence.toolVersion}. Not a content-integrity check.`,
        result.evidence.copyId,
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  function override(index: number, field: "language" | "role", value: string) {
    setOverrides((old) => ({
      ...old,
      [index]: { ...old[index], [field]: value || undefined },
    }));
  }
  return (
    <div className="media-review">
      <section className="media-card">
        <h2>Inspect and review</h2>
        <p>
          Understand the source, preview a preset and review every track. This
          inspection does not change media. Separately reviewed sample plans may
          create staged stream-copy or SDR x265 samples when explicitly enabled.
        </p>
        <div className="media-controls">
          <label>
            Catalog file (current page)
            <select
              value={copyId}
              onChange={(e) => setCopyId(e.target.value)}
              disabled={busy}
            >
              <option value="">Choose a file</option>
              {eligible.map((r) => (
                <option key={r.copy.id} value={r.copy.id}>
                  {r.copy.relativePath}
                </option>
              ))}
            </select>
          </label>
          <button
            onClick={inspect}
            disabled={
              busy || !eligible.some((r) => String(r.copy.id) === copyId)
            }
          >
            {busy ? "Inspecting…" : "Inspect read-only"}
          </button>
          <button
            disabled={busy}
            onClick={() =>
              acceptProbe(
                syntheticProbe,
                "Synthetic demonstration — invented metadata, not a real file.",
              )
            }
          >
            Load synthetic example
          </button>
        </div>
        <p>
          Live inspection requires a configured existing FFprobe binary.
          Imported metadata remains available without it.
        </p>
        <div className="media-controls">
          <label>
            Import FFprobe JSON
            <input
              type="file"
              accept=".json,application/json"
              disabled={busy}
              onChange={(e) => {
                void importFile(e.target.files?.[0], "probe");
                e.target.value = "";
              }}
            />
          </label>
          <label>
            Preview HandBrake preset
            <input
              type="file"
              accept=".json,application/json"
              disabled={busy}
              onChange={(e) => {
                void importFile(e.target.files?.[0], "preset");
                e.target.value = "";
              }}
            />
          </label>
        </div>
        {error && (
          <p role="alert" className="media-error">
            {error}
          </p>
        )}
      </section>
      {presets.map((p, i) => (
        <section className="media-card" key={`${p.name}-${i}`}>
          <h2>{p.name}</h2>
          <p>
            {p.container} · {p.encoder} · {p.bitDepth ?? "Unknown"}-bit ·{" "}
            {p.qualityMode} {p.quality ?? "?"} · {p.speed}
          </p>
          <dl className="media-facts">
            <dt>Profile</dt>
            <dd>{p.profile || "Unspecified"}</dd>
            <dt>Frame-rate mode</dt>
            <dd>{p.frameRateMode || "Unspecified"}</dd>
            <dt>Dimensions</dt>
            <dd>{p.dimensions}</dd>
            <dt>Crop</dt>
            <dd>
              {p.cropMode}; stored top / bottom / left / right: {p.storedCrop}
            </dd>
          </dl>
          {p.extra && <pre>{p.extra}</pre>}
          <ul>
            {p.warnings.map((warning) => (
              <li key={warning}>{warning}</li>
            ))}
          </ul>
          <details>
            <summary>Imported audio and subtitle rules</summary>
            <pre>
              {JSON.stringify(
                { audio: p.audioRules, subtitles: p.subtitleRules },
                null,
                2,
              )}
            </pre>
          </details>
        </section>
      ))}
      {inspection && (
        <section className="media-card">
          <h2>Technical inventory</h2>
          <p className="media-evidence">{origin}</p>
          <p>
            {inspection.format} ·{" "}
            {inspection.duration === null
              ? "Unknown duration"
              : `${inspection.duration.toFixed(2)} seconds`}{" "}
            ·{" "}
            {inspection.bytes === null
              ? "Unknown size"
              : bytes(inspection.bytes)}{" "}
            · {inspection.chapters.length} chapters
          </p>
          {inspection.tracks
            .filter((t) => t.type === "video")
            .map((t) => (
              <details key={t.index}>
                <summary>
                  Video {t.index}: {t.codec} · {String(t.details.width ?? "?")}{" "}
                  × {String(t.details.height ?? "?")} ·{" "}
                  {String(t.details.pix_fmt ?? "unknown pixel format")}
                </summary>
                <dl className="media-facts">
                  <dt>Codec profile</dt>
                  <dd>{String(t.details.profile ?? "Unknown")}</dd>
                  <dt>Average / nominal frame rate</dt>
                  <dd>
                    {String(t.details.avg_frame_rate ?? "Unknown")} /{" "}
                    {String(t.details.r_frame_rate ?? "Unknown")} (source
                    ratios)
                  </dd>
                  <dt>Color transfer / primaries / matrix</dt>
                  <dd>
                    {String(t.details.color_transfer ?? "Unknown")} /{" "}
                    {String(t.details.color_primaries ?? "Unknown")} /{" "}
                    {String(t.details.color_space ?? "Unknown")}
                  </dd>
                </dl>
                <p>
                  Reported stream metadata is shown below, including any HDR or
                  Dolby Vision side data. It does not establish preservation in
                  a future output.
                </p>
                <pre>{JSON.stringify(t.details, null, 2)}</pre>
              </details>
            ))}
          <h3>Track selection preview</h3>
          <p>
            Keep English and original-language soundtracks. Exclude confirmed
            commentary, retain accessibility audio, and review unknown roles or
            languages. Audio remains copy-only.
          </p>
          <label>
            Original language override (comma-separated codes, e.g. jpn, fra)
            <input
              value={originals}
              onChange={(e) => setOriginals(e.target.value)}
              placeholder="Confirm original language"
            />
          </label>
          {result?.error && (
            <p role="alert" className="media-error">
              {result.error}
            </p>
          )}
          {result?.review && (
            <>
              <p role="status">
                <strong>
                  {result.review.status === "preview-ready"
                    ? "Selection preview ready — no processing approved"
                    : "Review required"}
                </strong>
              </p>
              <ul>
                {result.review.blockers.map((b) => (
                  <li key={b}>{b}</li>
                ))}
              </ul>
            </>
          )}
          <div className="media-table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Stream</th>
                  <th>Language / role</th>
                  <th>Decision</th>
                  <th>Review correction</th>
                </tr>
              </thead>
              <tbody>
                {inspection.tracks.map((t) => {
                  const d = result?.review?.decisions.find(
                    (v) => v.index === t.index,
                  );
                  return (
                    <tr key={t.index}>
                      <td>
                        <strong>
                          #{t.index} {t.type}
                        </strong>
                        <br />
                        {t.codec}
                        {t.type === "audio" && (
                          <>
                            <br />
                            <small>
                              {String(
                                t.details.channel_layout ??
                                  t.details.channels ??
                                  "Unknown channels",
                              )}{" "}
                              · {String(t.details.sample_rate ?? "Unknown")} Hz
                            </small>
                          </>
                        )}
                        <br />
                        {t.title}
                        <br />
                        <small>{t.flags.join(", ")}</small>
                      </td>
                      <td>
                        {d?.language ?? t.language ?? "Unknown"}
                        {t.type === "audio" && (
                          <>
                            <br />
                            {d?.role ?? t.role}
                          </>
                        )}
                      </td>
                      <td>
                        <strong>{d?.action ?? "review"}</strong>
                        <p>
                          {d?.reason ??
                            "Resolve invalid corrections before evaluating."}
                        </p>
                      </td>
                      <td>
                        {["audio", "subtitle"].includes(t.type) && (
                          <label>
                            Language for stream {t.index}
                            <input
                              value={overrides[t.index]?.language ?? ""}
                              onChange={(e) =>
                                override(t.index, "language", e.target.value)
                              }
                              placeholder="Use detected tag"
                            />
                          </label>
                        )}
                        {t.type === "audio" && (
                          <label>
                            Role for stream {t.index}
                            <select
                              value={overrides[t.index]?.role ?? ""}
                              onChange={(e) =>
                                override(
                                  t.index,
                                  "role",
                                  e.target.value as TrackRole,
                                )
                              }
                            >
                              <option value="">Use detected role</option>
                              <option value="unknown">Unknown — review</option>
                              <option value="main">Main soundtrack</option>
                              <option value="commentary">
                                Confirmed commentary
                              </option>
                              <option value="descriptive">
                                Descriptive/accessibility
                              </option>
                            </select>
                          </label>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <p>
            Corrections are temporary review choices in this page. They do not
            rewrite source tags or alter catalog copy verification. Future
            optimized versions will be stored separately from originals.
          </p>
          <details>
            <summary>Complete source metadata</summary>
            <pre>{JSON.stringify(inspection.raw, null, 2)}</pre>
          </details>
        </section>
      )}
      {inspection && (
        <SampleRemuxReview
          copyId={liveCopyId}
          review={result?.review ?? null}
          originals={originals}
        />
      )}
    </div>
  );
}
