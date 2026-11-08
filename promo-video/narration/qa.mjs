// Objective QA for the narrated tutorial. Run after build.mjs (and optionally after a render):
//   node qa.mjs [path/to/rendered.mp4]
// Writes out/qa.json and prints a summary. Subjective qualities (how natural the voice sounds, pronunciation of
// individual words) still need a human listener; this checks what can be measured.
import fs from "node:fs";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
const require_spawn = (cmd, args) => spawnSync(cmd, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }).stderr || "";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const read = (p) => JSON.parse(fs.readFileSync(path.join(HERE, p), "utf8"));
const timing = read("out/timing.json");
const asr = read("out/asr.json");
const script = read("script.json");
const srt = fs.readFileSync(path.join(HERE, "out/subtitles.srt"), "utf8").replace(/^﻿/, "");
const video = process.argv[2];
const DIAC = /[ؐ-ًؚ-ٰٟۖ-ۭـ‪-‮]/g;
const norm = (t) => t.normalize("NFKC").replace(DIAC, "").replace(/[إأآٱ]/g, "ا").replace(/ى/g, "ي").replace(/ة/g, "ه").replace(/[^\p{L}\p{N}]+/gu, "");
const issues = [];
const res = { total: timing.total, checks: {} };

// 1. Intelligibility: Whisper's transcript of each line vs the words that were spoken.
const lines = script.scenes.flatMap((s) => s.lines);
const cers = lines.map((l) => ({ id: l.id, cer: asr[l.id]?.cer ?? null, heard: asr[l.id]?.text ?? "" }));
res.checks.intelligibility = { mean_cer: +(cers.reduce((a, x) => a + (x.cer ?? 0), 0) / cers.length).toFixed(4), worst: [...cers].sort((a, b) => b.cer - a.cer).slice(0, 5) };
for (const x of cers) if (x.cer === null || x.cer > 0.08) issues.push(`Line ${x.id}: Whisper CER ${x.cer} (heard: ${x.heard})`);

// 2. Subtitles: every subtitle sits inside its line's speech, never overlaps the next, and together they say the script.
const subs = srt.trim().split(/\n\n+/).map((b) => {
  const [, times, ...text] = b.split("\n");
  const [a, z] = times.split(" --> ").map((t) => { const [h, m, s] = t.replace(",", ".").split(":"); return +h * 3600 + +m * 60 + +s; });
  return { s: a, e: z, text: text.join(" ") };
});
let overlaps = 0, short = 0;
subs.forEach((x, i) => { if (subs[i + 1] && x.e > subs[i + 1].s) overlaps++; if (x.e - x.s < 0.8) short++; });
const spoken = norm(lines.map((l) => l.text).join(" "));
const shown = norm(subs.map((x) => x.text).join(" "));
res.checks.subtitles = { count: subs.length, overlaps, shorter_than_0_8s: short, text_matches_script: spoken === shown, max_chars: Math.max(...subs.map((x) => x.text.length)) };
if (overlaps) issues.push(`${overlaps} overlapping subtitles`);
if (spoken !== shown) issues.push("Subtitle text differs from the narration script");

// 3. Narration fits its scene (no speech cut by a transition) and no unplanned silence.
const gaps = [];
let clipped = 0;
let prevEnd = null;
for (const sc of timing.scenes) for (const l of sc.lines) {
  const s = sc.start + l.s, e = sc.start + l.e;
  if (l.e > sc.duration - 0.2) { clipped++; issues.push(`Line ${l.id} ends ${(sc.duration - l.e).toFixed(2)} s before its scene cut`); }
  if (prevEnd !== null) gaps.push({ before: l.id, gap: +(s - prevEnd).toFixed(2) });
  prevEnd = e;
}
const long = gaps.filter((g) => g.gap > 1.6);
res.checks.pacing = { clipped_lines: clipped, longest_gaps: [...gaps].sort((a, b) => b.gap - a.gap).slice(0, 6), speech_share: +(timing.scenes.flatMap((s) => s.lines).reduce((a, l) => a + l.d, 0) / timing.total).toFixed(3) };
for (const g of long) issues.push(`${g.gap} s of silence before ${g.before}`);

// 4. Actions happen in a sensible order relative to what they act on.
const tu = timing.scenes.find((s) => s.id === "tutorial").cues;
const order = [["dialogIn", "yesClick"], ["yesClick", "dialogOut"], ["pttClick", "testView"], ["printClick", "sending"], ["sending", "sentView"], ["sentView", "corners"], ["corners", "bars"], ["bars", "upright"], ["upright", "tray"], ["tray", "okClick"], ["softClick", "jobFound"], ["chooseView", "pickClick"], ["pickClick", "contClick"], ["netView", "typeStart"], ["typeEnd", "findClick"], ["confirmView", "yes2Click"], ["declinedView", "retryClick"], ["guidanceView", "statusView"], ["notReadyView", "copyClick"], ["detectDone", "checkRun"], ["checkDone", "installRun"], ["installDone", "configDone"], ["verifyDone", "readyDone"]];
const bad = order.filter(([a, b]) => !(tu[a] < tu[b]));
res.checks.action_order = { checked: order.length, out_of_order: bad };
for (const [a, b] of bad) issues.push(`Cue ${a} (${tu[a]}) is not before ${b} (${tu[b]})`);

// 5. The rendered file.
if (video && fs.existsSync(video)) {
  const probe = JSON.parse(execFileSync("ffprobe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height,r_frame_rate", "-of", "json", video]).toString());
  // ffmpeg prints its measurements on stderr; spawnSync keeps it whatever the exit code.
  const stderrOf = (args) => require_spawn("ffmpeg", args);
  const lj = stderrOf(["-hide_banner", "-nostats", "-i", video, "-af", "loudnorm=print_format=json", "-f", "null", "-"]);
  const m = /\{[\s\S]*\}/.exec(lj);
  const ln = m ? JSON.parse(m[0]) : {};
  const sil = stderrOf(["-hide_banner", "-nostats", "-i", video, "-af", "silencedetect=noise=-50dB:d=2", "-f", "null", "-"]);
  const silences = [...sil.matchAll(/silence_duration: ([\d.]+)/g)].map((x) => +x[1]);
  const dur = +probe.format.duration;
  res.checks.render = { file: path.basename(video), duration: dur, expected: timing.total, streams: probe.streams, integrated_lufs: +ln.input_i, true_peak_dbtp: +ln.input_tp, silences_over_2s: silences.length };
  if (Math.abs(dur - timing.total) > 0.25) issues.push(`Rendered duration ${dur} s vs timeline ${timing.total} s`);
  if (+ln.input_tp > -0.5) issues.push(`True peak ${ln.input_tp} dBTP`);
  if (silences.length) issues.push(`${silences.length} stretch(es) of near-silence over 2 s in the render`);
}

res.issues = issues;
fs.writeFileSync(path.join(HERE, "out/qa.json"), JSON.stringify(res, null, 1));
console.log(JSON.stringify(res.checks, null, 1).slice(0, 4000));
console.log(issues.length ? `\n${issues.length} issue(s):\n- ${issues.join("\n- ")}` : "\nNo issues found by the automated checks.");
