import test from "node:test";
import assert from "node:assert/strict";
import {
  inspectProbe,
  normalizeLanguage,
  parseMediaJson,
  previewPresets,
  reviewTracks,
} from "./mediaPolicy.ts";
import { syntheticProbe } from "./mediaFixture.ts";

const fixture = () => structuredClone(syntheticProbe);
const inspection = () => inspectProbe(fixture());
const decisions = () => reviewTracks(inspection(), ["jpn"]);

test("foreign original and English dub retained; commentary excluded; descriptive retained", () => {
  const review = decisions();
  assert.deepEqual(
    review.decisions.slice(1, 5).map((t) => t.action),
    ["keep", "keep", "exclude", "keep"],
  );
  assert.equal(review.audioHandling, "copy-only");
  assert.equal(review.executable, false);
});
test("regular, forced and SDH English subtitles remain separate", () => {
  const subtitles = decisions().decisions.filter((t) => t.type === "subtitle");
  assert.equal(subtitles.length, 3);
  assert.ok(subtitles.every((t) => t.action === "keep"));
  assert.ok(subtitles[1].flags.includes("forced"));
  assert.ok(subtitles[2].flags.includes("hearing_impaired"));
});
test("unknown language and role block approval until reviewed", () => {
  assert.equal(decisions().status, "review-required");
  const review = reviewTracks(inspection(), ["ja"], {
    8: { language: "en", role: "main" },
  });
  assert.equal(review.status, "preview-ready");
  assert.equal(review.executable, false);
});
test("foreign original cannot be silently dropped before original language is confirmed", () => {
  assert.equal(reviewTracks(inspection()).decisions[1].action, "review");
});
test("foreign film without English retains confirmed original and never becomes silent", () => {
  const value = fixture();
  value.streams = value.streams.filter((t) => [0, 1, 5].includes(t.index));
  const observed = inspectProbe(value);
  assert.equal(reviewTracks(observed, ["jpn"]).status, "preview-ready");
  assert.ok(
    reviewTracks(observed).blockers.some((v) =>
      v.includes("No confirmed main"),
    ),
  );
});
test("default disposition and stereo channels do not prove a main soundtrack", () => {
  const value = {
    streams: [
      { index: 0, codec_type: "video" },
      {
        index: 1,
        codec_type: "audio",
        channels: 2,
        tags: { language: "eng" },
        disposition: { default: 1 },
      },
    ],
  };
  assert.equal(reviewTracks(inspectProbe(value)).decisions[1].action, "review");
  const languageTitle = structuredClone(value);
  Object.assign(languageTitle.streams[1].tags!, { title: "English" });
  assert.equal(
    reviewTracks(inspectProbe(languageTitle)).decisions[1].action,
    "review",
  );
});
test("unknown-language commentary is still a review case", () => {
  const value = fixture();
  value.streams[3].tags!.language = "und";
  assert.equal(
    reviewTracks(inspectProbe(value), ["jpn"]).decisions[3].action,
    "review",
  );
});
test("conflicting commentary/accessibility evidence must be reviewed", () => {
  const value = {
    streams: [
      {
        index: 0,
        codec_type: "audio",
        tags: { language: "eng", title: "Commentary" },
        disposition: { visual_impaired: 1 },
      },
    ],
  };
  assert.equal(reviewTracks(inspectProbe(value)).decisions[0].action, "review");
});
test("accessibility audio is preserved even outside the main language choices", () => {
  const value = fixture();
  value.streams[4].tags!.language = "fra";
  assert.equal(
    reviewTracks(inspectProbe(value), ["jpn"]).decisions[4].action,
    "keep",
  );
});
test("unknown subtitle language stays under review even when forced", () => {
  const value = fixture();
  value.streams[6].tags!.language = "und";
  assert.equal(
    reviewTracks(inspectProbe(value), ["jpn"]).decisions[6].action,
    "review",
  );
});
test("language aliases normalize while unknown/multilingual and conflicting tags do not", () => {
  assert.equal(normalizeLanguage("EN-us"), "eng");
  assert.equal(normalizeLanguage("fre"), "fra");
  assert.equal(normalizeLanguage("dut"), normalizeLanguage("nl"));
  assert.equal(normalizeLanguage("swe"), normalizeLanguage("sv"));
  assert.equal(normalizeLanguage("qaa"), null);
  assert.equal(normalizeLanguage("zxx"), null);
  assert.equal(normalizeLanguage("mul"), null);
  assert.equal(normalizeLanguage("und"), null);
  assert.equal(normalizeLanguage("en,jpn"), null);
  const observed = inspectProbe({
    streams: [
      {
        index: 0,
        codec_type: "audio",
        tags: { language: "eng", "language-ietf": "ja", title: "Main" },
      },
    ],
  });
  assert.equal(observed.tracks[0].language, null);
});
test("attachments are retained; extra video and unknown stream kinds require review", () => {
  const value = fixture();
  assert.equal(decisions().decisions.at(-1)?.action, "keep");
  value.streams.push({ index: 10, codec_type: "video", codec_name: "h264" });
  assert.ok(
    reviewTracks(inspectProbe(value), ["jpn"]).blockers.includes(
      "Confirm the primary video stream.",
    ),
  );
});
test("probe accepts absent optional facts without inventing measurements", () => {
  const observed = inspectProbe({
    streams: [{ index: 0, codec_type: "video", avg_frame_rate: "0/0" }],
  });
  assert.equal(observed.bytes, null);
  assert.equal(observed.duration, null);
  assert.deepEqual(observed.chapters, []);
  assert.equal(observed.tracks[0].details.avg_frame_rate, "0/0");
});
test("invalid, oversized, error and duplicate-index probes are rejected", () => {
  for (const invalid of [
    {},
    { streams: [] },
    { error: {}, streams: [{}] },
    { streams: [{ index: -1 }] },
    { streams: [{ index: 0 }, { index: 0 }] },
    { streams: Array.from({ length: 257 }, (_, index) => ({ index })) },
  ])
    assert.throws(() => inspectProbe(invalid));
  assert.throws(() => parseMediaJson(" ".repeat(4 * 1024 * 1024 + 1)));
  assert.throws(() => parseMediaJson("[".repeat(70) + "0" + "]".repeat(70)));
});
test("review does not mutate input observations and refuses stale or invalid corrections", () => {
  const value = fixture(),
    before = JSON.stringify(value),
    observed = inspectProbe(value);
  reviewTracks(observed, ["jpn"], { 8: { language: "eng", role: "main" } });
  assert.equal(JSON.stringify(value), before);
  assert.throws(() => reviewTracks(observed, [], { 99: { role: "main" } }));
  assert.throws(() => reviewTracks(observed, ["und"]));
  assert.throws(() => reviewTracks(observed, [], { 8: { language: "typo" } }));
});
const preset = (encoder = "x265") => ({
  PresetList: [
    {
      PresetName: "Synthetic preset",
      VideoEncoder: encoder,
      VideoQualityType: 2,
      VideoQualitySlider: 22,
      VideoPreset: "slow",
      VideoProfile: "auto",
      VideoFramerateMode: "vfr",
      VideoMultiPass: true,
      FileFormat: "av_mkv",
      PictureWidth: 1920,
      PictureHeight: 1080,
      PictureCropMode: 0,
      PictureTopCrop: 20,
      PictureBottomCrop: 22,
      PictureLeftCrop: 0,
      PictureRightCrop: 0,
      PictureAllowUpscaling: false,
      AudioList: [{ AudioEncoder: "av_aac" }, { AudioEncoder: "copy" }],
      AudioEncoderFallback: "av_aac",
      AudioTrackSelectionBehavior: "first",
      SubtitleTrackSelectionBehavior: "none",
      SubtitleLanguageList: [],
      SubtitleBurnBehavior: "foreign",
    },
  ],
});
test("preset preview distinguishes 8-bit/10-bit x265 and reports actual conflicting rules", () => {
  const input = preset(),
    before = JSON.stringify(input),
    p = previewPresets(input)[0];
  assert.equal(p.bitDepth, 8);
  assert.equal(p.quality, 22);
  assert.equal(p.cropMode, "automatic");
  assert.equal(p.storedCrop, "20 / 22 / 0 / 0");
  assert.ok(p.warnings.some((v) => v.includes("encoded audio")));
  assert.ok(p.warnings.some((v) => v.includes("fallback")));
  assert.ok(p.warnings.some((v) => v.includes("all English")));
  assert.ok(p.warnings.some((v) => v.includes("Multipass is inactive")));
  assert.equal(previewPresets(preset("x265_10bit"))[0].bitDepth, 10);
  assert.equal(JSON.stringify(input), before);
});
test("nested preset folders work; unknown encoders do not inherit x265 quality assumptions", () => {
  const p = previewPresets({
    PresetList: [
      { Folder: true, ChildrenArray: preset("nvenc_h265").PresetList },
    ],
  })[0];
  assert.equal(p.bitDepth, null);
  assert.ok(p.warnings.some((v) => v.includes("not interchangeable")));
  assert.throws(() => previewPresets({ PresetList: [] }));
});
