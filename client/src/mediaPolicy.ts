// Review-only domain. Decisions are never executable stream maps or copy evidence.
export type JsonRecord = Record<string, unknown>;
export type TrackRole = "main" | "commentary" | "descriptive" | "unknown";
export type Track = {
  index: number;
  type: string;
  codec: string;
  language: string | null;
  title: string;
  role: TrackRole;
  flags: string[];
  details: JsonRecord;
};
export type Inspection = {
  format: string;
  duration: number | null;
  bytes: number | null;
  tracks: Track[];
  chapters: unknown[];
  raw: JsonRecord;
};
export type TrackOverride = { language?: string; role?: TrackRole };
export type TrackDecision = Track & {
  action: "keep" | "exclude" | "review";
  reason: string;
};
export type TrackReview = {
  decisions: TrackDecision[];
  blockers: string[];
  notes: string[];
  status: "review-required" | "preview-ready";
  audioHandling: "copy-only";
  executable: false;
};
const record = (value: unknown): JsonRecord => {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Expected a JSON object.");
  return value as JsonRecord;
};
const text = (value: unknown): string =>
  typeof value === "string" ? value : "";
const numeric = (value: unknown): number | null => {
  if (
    value === null ||
    value === undefined ||
    value === "" ||
    typeof value === "boolean"
  )
    return null;
  const n = Number(value);
  return Number.isFinite(n) && n >= 0 ? n : null;
};
export function parseMediaJson(input: string): unknown {
  if (new TextEncoder().encode(input).length > 4 * 1024 * 1024)
    throw new Error("JSON exceeds the 4 MiB review limit.");
  const parsed: unknown = JSON.parse(input);
  const pending: [unknown, number][] = [[parsed, 0]];
  let nodes = 0;
  while (pending.length) {
    const [value, depth] = pending.pop()!;
    if (++nodes > 50000 || depth > 64)
      throw new Error("JSON structure exceeds the review limit.");
    if (value && typeof value === "object")
      for (const child of Object.values(value))
        pending.push([child, depth + 1]);
  }
  return parsed;
}
const languageAliases: Record<string, string> = {
  en: "eng",
  eng: "eng",
  ja: "jpn",
  jpn: "jpn",
  fr: "fra",
  fre: "fra",
  fra: "fra",
  de: "deu",
  ger: "deu",
  deu: "deu",
  es: "spa",
  spa: "spa",
  ko: "kor",
  kor: "kor",
  zh: "zho",
  chi: "zho",
  zho: "zho",
  it: "ita",
  ita: "ita",
  pt: "por",
  por: "por",
  ru: "rus",
  rus: "rus",
  hi: "hin",
  hin: "hin",
  ar: "ara",
  ara: "ara",
};
// Canonicalize ISO aliases with the runtime's language catalogue; never treat a
// syntactically valid but unknown/private/multiple-language tag as a known language.
export function normalizeLanguage(value: unknown): string | null {
  const tag = text(value).trim().toLowerCase();
  if (!/^[a-z]{2,3}(?:-[a-z0-9]{2,8})*$/.test(tag)) return null;
  try {
    const canonical = new Intl.Locale(tag).language;
    if (["und", "mul", "zxx", "mis"].includes(canonical)) return null;
    if (
      !new Intl.DisplayNames(["en"], { type: "language", fallback: "none" }).of(
        canonical,
      )
    )
      return null;
    return languageAliases[canonical] ?? canonical;
  } catch {
    return null;
  }
}
function inferRole(title: string, flags: string[]): TrackRole {
  const commentary = flags.includes("comment") || /\bcommentary\b/i.test(title);
  const descriptive =
    flags.includes("visual_impaired") ||
    /\b(audio description|descriptive audio|described audio)\b/i.test(title);
  if (commentary && descriptive) return "unknown";
  if (commentary) return "commentary";
  if (descriptive) return "descriptive";
  if (
    /^(main|main audio|original|original audio)( soundtrack)?$/i.test(
      title.trim(),
    )
  )
    return "main";
  // Channel count, default disposition and stream order do not establish a track's role.
  return "unknown";
}
export function inspectProbe(value: unknown): Inspection {
  const raw = record(value);
  if (raw.error)
    throw new Error(
      "Probe reported an error; this is not usable media evidence.",
    );
  if (
    !Array.isArray(raw.streams) ||
    !raw.streams.length ||
    raw.streams.length > 256
  )
    throw new Error("Probe must contain 1–256 streams.");
  const indices = new Set<number>();
  const tracks = raw.streams.map((item): Track => {
    const stream = record(item),
      index = stream.index;
    if (
      !Number.isSafeInteger(index) ||
      Number(index) < 0 ||
      indices.has(Number(index))
    )
      throw new Error("Invalid or duplicate stream index.");
    indices.add(Number(index));
    const tags = stream.tags ? record(stream.tags) : {};
    const dispositions = stream.disposition ? record(stream.disposition) : {};
    const flags = Object.entries(dispositions)
      .filter(([, v]) => v === 1 || v === true)
      .map(([k]) => k);
    const title = text(tags.title);
    const isoLanguage = normalizeLanguage(tags.language);
    const ietfLanguage = normalizeLanguage(tags["language-ietf"]);
    const conflicting =
      tags.language &&
      tags["language-ietf"] &&
      (!isoLanguage || !ietfLanguage || isoLanguage !== ietfLanguage);
    return {
      index: Number(index),
      type: text(stream.codec_type) || "unknown",
      codec: text(stream.codec_name) || "unknown",
      language: conflicting ? null : (isoLanguage ?? ietfLanguage),
      title,
      role: inferRole(title, flags),
      flags,
      details: stream,
    };
  });
  const format = raw.format ? record(raw.format) : {};
  if (
    raw.chapters !== undefined &&
    (!Array.isArray(raw.chapters) || raw.chapters.length > 4096)
  )
    throw new Error("Invalid or oversized chapters collection.");
  return {
    format: text(format.format_name) || "unknown",
    duration: numeric(format.duration),
    bytes: numeric(format.size),
    tracks,
    chapters: (raw.chapters ?? []) as unknown[],
    raw,
  };
}
export function reviewTracks(
  inspection: Inspection,
  originalLanguages: string[] = [],
  overrides: Record<number, TrackOverride> = {},
): TrackReview {
  const originals = new Set(
    originalLanguages.map((l) => {
      const language = normalizeLanguage(l);
      if (!language) throw new Error(`Unrecognized original language: ${l}`);
      return language;
    }),
  );
  for (const [key, override] of Object.entries(overrides)) {
    if (!inspection.tracks.some((t) => t.index === Number(key)))
      throw new Error("Override refers to a missing stream.");
    if (
      override.role &&
      !["main", "commentary", "descriptive", "unknown"].includes(override.role)
    )
      throw new Error("Invalid track role.");
    if (override.language && !normalizeLanguage(override.language))
      throw new Error("Unrecognized override language.");
  }
  const decisions = inspection.tracks.map((track): TrackDecision => {
    const override = overrides[track.index];
    const language = override?.language
      ? normalizeLanguage(override.language)
      : track.language;
    const role = override?.role ?? track.role;
    const base = { ...track, language, role };
    const decide = (
      action: TrackDecision["action"],
      reason: string,
    ): TrackDecision => ({ ...base, action, reason });
    if (track.type === "video")
      return decide(
        track.flags.includes("attached_pic") ? "review" : "keep",
        track.flags.includes("attached_pic")
          ? "Cover artwork: review separately from primary video."
          : "Preserve source video pending a separate processing plan.",
      );
    if (track.type === "audio") {
      if (role === "unknown")
        return decide(
          "review",
          "Confirm track role; unknown roles are never silently dropped.",
        );
      if (!language)
        return decide(
          "review",
          "Missing, unfamiliar or conflicting language tags.",
        );
      if (role === "commentary")
        return decide(
          "exclude",
          "Identified as commentary; exclude only from a future optimized variant.",
        );
      if (role === "descriptive")
        return decide(
          "keep",
          "Retain accessibility audio; removal has not been authorized.",
        );
      if (language === "eng" || originals.has(language))
        return decide(
          "keep",
          "Keep English or explicitly selected original-language audio; stream copy only.",
        );
      if (!originals.size)
        return decide(
          "review",
          "Confirm original language before excluding a foreign soundtrack.",
        );
      return decide(
        "exclude",
        "Other-language main audio outside the English/original-language selection.",
      );
    }
    if (track.type === "subtitle") {
      if (!language)
        return decide(
          "review",
          "Confirm subtitle language; forced/SDH flags do not establish language.",
        );
      return language === "eng"
        ? decide(
            "keep",
            "Keep English subtitles, including forced and SDH, as separate tracks.",
          )
        : decide(
            "exclude",
            "Non-English subtitle; original source remains unchanged.",
          );
    }
    if (track.type === "attachment")
      return decide(
        "keep",
        "Preserve attachments that may be required for styled subtitles.",
      );
    return decide("review", "Unrecognized stream type requires review.");
  });
  const blockers: string[] = [];
  if (
    !decisions.some(
      (t) => t.type === "audio" && t.action === "keep" && t.role === "main",
    )
  )
    blockers.push(
      "No confirmed main soundtrack selected. Preserve source and review; never create a silent or commentary-only output.",
    );
  if (decisions.some((t) => t.action === "review"))
    blockers.push(
      "Resolve every flagged stream before any future processing approval.",
    );
  if (
    decisions.filter(
      (t) => t.type === "video" && !t.flags.includes("attached_pic"),
    ).length !== 1
  )
    blockers.push("Confirm the primary video stream.");
  return {
    decisions,
    blockers,
    notes: [
      "Preview only; no media changes or processing command is created.",
      "Container compatibility, HDR preservation and playback validation are separate future gates.",
      "Original files stay intact. Disc archives will use validated optimized variants.",
    ],
    status: blockers.length ? "review-required" : "preview-ready",
    audioHandling: "copy-only",
    executable: false,
  };
}
export type PresetPreview = {
  name: string;
  container: string;
  encoder: string;
  bitDepth: number | null;
  quality: number | null;
  qualityMode: string;
  speed: string;
  profile: string;
  frameRateMode: string;
  dimensions: string;
  cropMode: string;
  storedCrop: string;
  extra: string;
  audioRules: unknown;
  subtitleRules: unknown;
  warnings: string[];
  raw: JsonRecord;
};
export function previewPresets(value: unknown): PresetPreview[] {
  const root = record(value);
  const output: PresetPreview[] = [];
  const visit = (list: unknown, depth: number) => {
    if (!Array.isArray(list) || depth > 8 || list.length > 100)
      throw new Error("Invalid or oversized preset collection.");
    for (const item of list) {
      const p = record(item);
      if (p.Folder === true) {
        visit(p.ChildrenArray, depth + 1);
        continue;
      }
      if (output.length >= 100) throw new Error("Too many presets.");
      const encoder = text(p.VideoEncoder),
        constant = p.VideoQualityType === 2;
      const warnings: string[] = [];
      const audio = Array.isArray(p.AudioList) ? p.AudioList.map(record) : [];
      if (audio.some((a) => !text(a.AudioEncoder).startsWith("copy")))
        warnings.push(
          "Preset creates encoded audio. Library policy requires copy-only retained audio.",
        );
      if (p.AudioEncoderFallback && p.AudioEncoderFallback !== "none")
        warnings.push(
          "Audio fallback encoder is not permitted; incompatible audio must stop for review.",
        );
      if (p.AudioTrackSelectionBehavior === "first")
        warnings.push(
          "First-match selection can omit desired tracks or fall back to the first track. Review explicit streams.",
        );
      if (
        p.SubtitleBurnBehavior !== "none" &&
        p.SubtitleBurnBehavior !== undefined
      )
        warnings.push(
          "Subtitle burn behavior conflicts with separate English subtitle preservation.",
        );
      if (
        p.SubtitleTrackSelectionBehavior !== "all" ||
        !Array.isArray(p.SubtitleLanguageList) ||
        !p.SubtitleLanguageList.includes("eng")
      )
        warnings.push(
          "Preset does not select all English subtitles; use the reviewed English regular/forced/SDH policy.",
        );
      if (p.PictureCropMode === 0)
        warnings.push(
          "Automatic crop: stored crop numbers are not a fixed crop for every source.",
        );
      if (constant && p.VideoMultiPass === true && encoder.startsWith("x265"))
        warnings.push("Multipass is inactive for x265 constant-quality mode.");
      if (!["x265", "x265_10bit"].includes(encoder))
        warnings.push(
          "Encoder has not been qualified for this milestone; quality scales are not interchangeable.",
        );
      warnings.push(
        "Import is a preview, not an executable FFmpeg profile. HDR metadata and client compatibility require qualification.",
      );
      output.push({
        name: text(p.PresetName) || "Unnamed preset",
        container: text(p.FileFormat),
        encoder,
        bitDepth: encoder === "x265" ? 8 : encoder === "x265_10bit" ? 10 : null,
        quality: numeric(p.VideoQualitySlider),
        qualityMode: constant ? "constant-quality" : "unqualified",
        speed: text(p.VideoPreset),
        profile: text(p.VideoProfile),
        frameRateMode: text(p.VideoFramerateMode),
        dimensions: `${p.PictureWidth ?? "?"} × ${p.PictureHeight ?? "?"}; upscale ${p.PictureAllowUpscaling === true ? "enabled" : "disabled"}`,
        cropMode:
          (
            { 0: "automatic", 1: "loose", 2: "none", 3: "custom" } as Record<
              number,
              string
            >
          )[Number(p.PictureCropMode)] ?? "unknown",
        storedCrop: [
          p.PictureTopCrop,
          p.PictureBottomCrop,
          p.PictureLeftCrop,
          p.PictureRightCrop,
        ]
          .map((v) => v ?? "?")
          .join(" / "),
        extra: text(p.VideoOptionExtra),
        audioRules: {
          selection: p.AudioTrackSelectionBehavior,
          languages: p.AudioLanguageList,
          tracks: p.AudioList,
          copyMask: p.AudioCopyMask,
          fallback: p.AudioEncoderFallback,
        },
        subtitleRules: {
          selection: p.SubtitleTrackSelectionBehavior,
          languages: p.SubtitleLanguageList,
          foreignSearch: p.SubtitleAddForeignAudioSearch,
          burn: p.SubtitleBurnBehavior,
        },
        warnings,
        raw: p,
      });
    }
  };
  visit(root.PresetList, 0);
  if (!output.length) throw new Error("No leaf presets found.");
  return output;
}
