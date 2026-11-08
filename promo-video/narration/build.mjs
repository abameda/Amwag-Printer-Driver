// Narration-driven timeline builder for the Egyptian Arabic tutorial (composition-eg).
//
//   node build.mjs            # after tts.py and asr.py (run.sh does all of it)
//
// Reads  script.json (narration), scenes.json (what each line shows), tts-config.json (voice),
//        out/voice/*.wav + manifest.json (measured speech), out/asr.json (Whisper word timings), beats.mjs (animation rules)
// Writes ../composition-eg/index.html, ../composition-eg/compositions/eg-captions.html,
//        ../composition-eg/assets/voice/voiceover.wav, ../composition-eg/assets/music/bed-ducked.wav,
//        out/timing.json, out/subtitles.srt, out/storyboard.md
// Nothing here is hand-timed: change a sentence, re-run, and the picture, sounds and captions follow it.
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { scenes as BEATS } from "./beats.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PROJECT = path.join(HERE, "..", "composition-eg");
const OUT = path.join(HERE, "out");
const VOICE_DIR = path.join(OUT, "voice");
const read = (p) => JSON.parse(fs.readFileSync(p, "utf8"));
const r3 = (x) => Math.round(x * 1000) / 1000;

const script = read(path.join(HERE, "script.json"));
const meta = read(path.join(HERE, "scenes.json"));
const manifest = read(path.join(VOICE_DIR, "manifest.json"));
const asr = fs.existsSync(path.join(OUT, "asr.json")) ? read(path.join(OUT, "asr.json")) : {};

// ---------- Arabic text helpers ----------
const DIAC = /[ؐ-ًؚ-ٰٟۖ-ۭـ]/g;
const norm = (w) => w.normalize("NFKC").replace(DIAC, "").replace(/[إأآٱ]/g, "ا").replace(/ى/g, "ي").replace(/ة/g, "ه")
  .replace(/[^\p{L}\p{N}]/gu, "").toLowerCase();
function similarity(a, b) {
  if (!a || !b) return 0;
  if (a === b) return 1;
  if (a.includes(b) || b.includes(a)) return Math.min(a.length, b.length) / Math.max(a.length, b.length) + 0.15;
  const m = a.length, n = b.length;
  let prev = Array.from({ length: n + 1 }, (_, j) => j);
  for (let i = 1; i <= m; i++) {
    const cur = [i];
    for (let j = 1; j <= n; j++) cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] !== b[j - 1]));
    prev = cur;
  }
  return 1 - prev[n] / Math.max(m, n);
}

// Word times for a line's display text: align its words to Whisper's words (dynamic programming on similarity), and
// interpolate by character position where Whisper heard something else. Returns [{ w, s, e }] in line time.
function wordTimes(line, dur) {
  const words = line.text.split(/\s+/).filter(Boolean);
  const total = words.reduce((n, w) => n + w.length + 1, 0);
  let acc = 0;
  const est = words.map((w) => { const s = (acc / total) * dur; acc += w.length + 1; return { w, s, e: (acc / total) * dur }; });
  const heard = (asr[line.id]?.words || []).filter((x) => norm(x.w));
  if (!heard.length) return est;
  const A = words.map(norm), B = heard.map((x) => norm(x.w));
  const n = A.length, m = B.length;
  const score = Array.from({ length: n + 1 }, () => new Float64Array(m + 1));
  const move = Array.from({ length: n + 1 }, () => new Int8Array(m + 1));
  for (let i = 1; i <= n; i++) { score[i][0] = 0; move[i][0] = 1; }
  for (let j = 1; j <= m; j++) { score[0][j] = 0; move[0][j] = 2; }
  for (let i = 1; i <= n; i++) for (let j = 1; j <= m; j++) {
    const sim = similarity(A[i - 1], B[j - 1]);
    const diag = score[i - 1][j - 1] + (sim >= 0.55 ? sim : -0.2);
    const up = score[i - 1][j], left = score[i][j - 1];
    if (diag >= up && diag >= left) { score[i][j] = diag; move[i][j] = 0; } else if (up >= left) { score[i][j] = up; move[i][j] = 1; } else { score[i][j] = left; move[i][j] = 2; }
  }
  const match = new Array(n).fill(null);
  for (let i = n, j = m; i > 0 && j > 0;) {
    if (move[i][j] === 0) { if (similarity(A[i - 1], B[j - 1]) >= 0.55) match[i - 1] = heard[j - 1]; i--; j--; } else if (move[i][j] === 1) i--; else j--;
  }
  const out = est.map((x, i) => (match[i] ? { w: x.w, s: match[i].s, e: Math.max(match[i].e, match[i].s + 0.05) } : { ...x, guess: true }));
  // Re-place unmatched words between their matched neighbours, keeping their share of characters.
  for (let i = 0; i < n; i++) {
    if (!out[i].guess) continue;
    let a = i - 1; while (a >= 0 && out[a].guess) a--;
    let b = i; while (b < n && out[b].guess) b++;
    const t0 = a >= 0 ? out[a].e : 0, t1 = b < n ? out[b].s : dur;
    const span = words.slice(a + 1, b).reduce((k, w) => k + w.length + 1, 0);
    let c = 0;
    for (let k = a + 1; k < b; k++) {
      const s = t0 + (c / span) * (t1 - t0); c += words[k].length + 1;
      out[k] = { w: words[k], s, e: t0 + (c / span) * (t1 - t0), guess: true };
    }
    i = b - 1;
  }
  return out;
}

// ---------- 1. Lay out the timeline from measured speech ----------
const timeline = [];
let t = 0;
for (const sc of script.scenes) {
  const scene = { id: sc.id, file: sc.file, start: r3(t), lines: [] };
  let local = sc.lead ?? 0.5;
  for (const l of sc.lines) {
    const m = manifest[l.id];
    if (!m) throw new Error(`No audio for ${l.id}: run tts.py first`);
    const words = wordTimes(l, m.duration);
    scene.lines.push({ ...l, s: r3(local), e: r3(local + m.duration), d: m.duration, words });
    local += m.duration + (l.pause ?? 0.35);
  }
  const last = scene.lines[scene.lines.length - 1];
  scene.duration = r3(last.e + (sc.tail ?? 0.6));
  t += scene.duration;
  timeline.push(scene);
}
const TOTAL = r3(t);

// ---------- 2. Anchors and animation cues ----------
const anchorTime = (line, word) => {
  const target = norm(word);
  const hit = line.words.find((w) => norm(w.w).includes(target) || similarity(norm(w.w), target) >= 0.75);
  if (!hit) throw new Error(`Anchor "${word}" not found in ${line.id}`);
  return hit.s;
};
const report = [];
for (const scene of timeline) {
  const byId = Object.fromEntries(scene.lines.map((l) => [l.id, l]));
  const L = (id) => { const l = byId[id]; if (!l) throw new Error(`${scene.id}: unknown line ${id}`); return { s: l.s, e: l.e, d: l.d }; };
  const A = (id, name) => {
    const l = byId[id]; const word = l?.anchors?.[name];
    if (!word) throw new Error(`${scene.id}: no anchor ${name} on ${id}`);
    const at = l.s + anchorTime(l, word);
    if (!report.some((x) => x.line === id && x.anchor === name)) report.push({ line: id, anchor: name, word, at: r3(at - l.s), guessed: l.words.find((w) => norm(w.w).includes(norm(word)))?.guess === true });
    return at;
  };
  const res = BEATS[scene.id]({ L, A, dur: scene.duration });
  scene.cues = res.cues;
  scene.sfx = res.sfx;
  scene.cues.lines = Object.fromEntries(scene.lines.map((l) => [l.id, [l.s, l.e]]));
  // Step captions beside the app window (scenes.json sections), timed to the line that opens them.
  scene.cues.sections = meta.sections.filter((x) => x.scene === scene.id).map((x) => ({ ...x, at: r3(Math.max(0.15, L(x.from).s - 0.2)) }));
}

// ---------- 3. Subtitles ----------
const MAX_CHARS = 52;
function chunks(line) {
  // Break at punctuation first, then at word boundaries, so each subtitle is one short readable phrase.
  const words = line.words;
  const groups = [];
  let cur = [];
  const len = (g) => g.map((w) => w.w).join(" ").length;
  words.forEach((w, i) => {
    cur.push(w);
    const end = /[،,.:؟?!]$/.test(w.w);
    const next = words[i + 1];
    if (i === words.length - 1 || (end && len(cur) >= 14) || (next && len([...cur, next]) > MAX_CHARS)) { groups.push(cur); cur = []; }
  });
  if (cur.length) groups.push(cur);
  // Merge a very short tail into the previous group when it still fits.
  for (let i = groups.length - 1; i > 0; i--) if (len(groups[i]) < 12 && len([...groups[i - 1], ...groups[i]]) <= MAX_CHARS + 8) { groups[i - 1].push(...groups[i]); groups.splice(i, 1); }
  return groups.map((g) => ({ text: g.map((w) => w.w).join(" "), s: g[0].s, e: g[g.length - 1].e }));
}
const subs = [];
for (const scene of timeline) for (const l of scene.lines) for (const c of chunks(l)) subs.push({ text: c.text, s: scene.start + l.s + c.s, e: scene.start + l.s + c.e, line: l.id });
// Show each subtitle a hair before its first word, keep it a little after its last, never overlap the next one,
// and bridge short gaps so the caption box does not flicker between phrases.
for (const x of subs) x.s = r3(Math.max(0, x.s - 0.05));
for (let i = 0; i < subs.length; i++) {
  const next = subs[i + 1];
  subs[i].e = r3(Math.max(subs[i].e + 0.25, subs[i].s + 0.9));
  if (next && (subs[i].e > next.s - 0.04 || next.s - subs[i].e < 0.35)) subs[i].e = r3(next.s - 0.04);
}
const srtTime = (x) => { const ms = Math.round(x * 1000); const h = Math.floor(ms / 3600000), m = Math.floor(ms / 60000) % 60, s = Math.floor(ms / 1000) % 60; return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")},${String(ms % 1000).padStart(3, "0")}`; };
const srt = subs.map((x, i) => `${i + 1}\n${srtTime(x.s)} --> ${srtTime(x.e)}\n‫${x.text}‬\n`).join("\n");

// ---------- 4. Audio: voiceover and ducked music bed ----------
fs.mkdirSync(path.join(PROJECT, "assets", "voice"), { recursive: true });
fs.mkdirSync(path.join(PROJECT, "assets", "music"), { recursive: true });
const voiceOut = path.join(PROJECT, "assets", "voice", "voiceover.wav");
{
  const inputs = [], filters = [];
  let k = 0;
  for (const scene of timeline) for (const l of scene.lines) {
    inputs.push("-i", path.join(VOICE_DIR, `${l.id}.wav`));
    const ms = Math.round((scene.start + l.s) * 1000);
    filters.push(`[${k}:a]aresample=48000,pan=mono|c0=c0,adelay=${ms}|${ms}[v${k}]`);
    k++;
  }
  const mix = `${Array.from({ length: k }, (_, i) => `[v${i}]`).join("")}amix=inputs=${k}:normalize=0:dropout_transition=0,apad=whole_dur=${TOTAL},atrim=0:${TOTAL}[out]`;
  execFileSync("ffmpeg", ["-v", "error", "-y", ...inputs, "-filter_complex", [...filters, mix].join(";"), "-map", "[out]", "-ar", "48000", "-ac", "1", voiceOut]);
}
// Music: the custom instrumental, lengthened by looping its full-band section on bar lines, then ducked under the voice.
const MUSIC = { file: path.join(PROJECT, "assets", "music", "amwag-custom-bed.mp3"), loopStart: 18.3, loopEnd: 54.0, endStart: 54.0, xfade: 1.2 };
const musicOut = path.join(PROJECT, "assets", "music", "bed-ducked.wav");
{
  const intro = MUSIC.loopStart, body = MUSIC.loopEnd - MUSIC.loopStart, endLen = 60.0 - MUSIC.endStart;
  const need = TOTAL - intro - endLen + MUSIC.xfade;
  const reps = Math.max(1, Math.ceil(need / (body - MUSIC.xfade)) + 1);
  const parts = [`[0:a]atrim=0:${intro + MUSIC.xfade},asetpts=PTS-STARTPTS[p0]`];
  for (let i = 1; i <= reps; i++) parts.push(`[0:a]atrim=${MUSIC.loopStart - MUSIC.xfade / 2}:${MUSIC.loopEnd + MUSIC.xfade / 2},asetpts=PTS-STARTPTS[p${i}]`);
  let chain = "[p0]";
  for (let i = 1; i <= reps; i++) { parts.push(`${chain}[p${i}]acrossfade=d=${MUSIC.xfade}:c1=tri:c2=tri[c${i}]`); chain = `[c${i}]`; }
  const bodyEnd = TOTAL - endLen + MUSIC.xfade;
  parts.push(`${chain}atrim=0:${bodyEnd},asetpts=PTS-STARTPTS[long]`);
  parts.push(`[0:a]atrim=${MUSIC.endStart - MUSIC.xfade}:60,asetpts=PTS-STARTPTS[ending]`);
  parts.push(`[long][ending]acrossfade=d=${MUSIC.xfade}:c1=tri:c2=tri,aresample=48000,aformat=channel_layouts=stereo[music]`);
  parts.push(`[1:a]aresample=48000,aformat=channel_layouts=stereo[sc]`);
  parts.push(`[music][sc]sidechaincompress=threshold=0.015:ratio=10:attack=40:release=650:makeup=1[ducked]`);
  parts.push(`[ducked]atrim=0:${TOTAL}[mout]`);
  execFileSync("ffmpeg", ["-v", "error", "-y", "-i", MUSIC.file, "-i", voiceOut, "-filter_complex", parts.join(";"), "-map", "[mout]", "-ar", "48000", musicOut]);
}

// ---------- 5. The composition ----------
const esc = (s) => s.replace(/&/g, "&amp;").replace(/'/g, "&#39;").replace(/</g, "&lt;");
const hosts = timeline.map((sc) => {
  const vars = esc(JSON.stringify({ lang: "ar", cues: JSON.stringify(sc.cues) }));
  const id = sc.file.replace(/\.html$/, "");
  return `      <div id="${id}" class="clip" data-composition-id="${id}" data-composition-src="compositions/${sc.file}"
           data-variable-values='${vars}'
           data-start="${sc.start}" data-duration="${sc.duration}" data-track-index="1" data-track-kind="graphics" data-width="1920" data-height="1080"></div>`;
}).join("\n");
const sfxTags = timeline.flatMap((sc) => sc.sfx.map((x) => ({ ...x, t: r3(sc.start + x.t) }))).sort((a, b) => a.t - b.t)
  .map((x, i) => `      <audio id="sfx-${String(i + 1).padStart(3, "0")}" class="clip" src="assets/sfx/${x.name}.ogg" data-start="${x.t}" data-duration="${x.name === "bell" ? 2.5 : 1.2}" data-track-index="4" data-volume="${x.vol}"></audio>`).join("\n");
const index = `<!doctype html>
<!-- Generated by ../narration/build.mjs from the narration: do not edit; change narration/*.json or beats.mjs and rebuild. -->
<html lang="ar" dir="rtl">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1920, height=1080" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.15.0/dist/gsap.min.js"></script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { margin: 0; width: 1920px; height: 1080px; overflow: hidden; background: #0f2a4a; }
      #root { position: relative; width: 100%; height: 100%; }
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="${TOTAL}" data-width="1920" data-height="1080">
${hosts}
      <div id="eg-captions" class="clip" data-composition-id="eg-captions" data-composition-src="compositions/eg-captions.html"
           data-start="0" data-duration="${TOTAL}" data-track-index="2" data-track-kind="captions" data-width="1920" data-height="1080"></div>
      <audio id="voiceover" class="clip" src="assets/voice/voiceover.wav" data-start="0" data-duration="${TOTAL}" data-track-index="3" data-volume="1"></audio>
      <audio id="music-bed" class="clip" src="assets/music/bed-ducked.wav" data-start="0" data-duration="${TOTAL}" data-track-index="5" data-volume="0.32" data-fade-in="0.3" data-fade-out="1.5"></audio>
${sfxTags}
    </div>
    <script>
      window.__timelines = window.__timelines || {};
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`;
fs.writeFileSync(path.join(PROJECT, "index.html"), index);

// Captions scene: one timed block per subtitle, bottom centre, right-to-left.
const capEls = subs.map((x, i) => `    <div id="cap-${String(i + 1).padStart(3, "0")}" class="clip cap" data-start="${x.s}" data-duration="${r3(x.e - x.s)}" data-track-index="0"><span>${esc(x.text)}</span></div>`).join("\n");
const captions = `<template>
  <style>
    @font-face { font-family: "AmwagUI"; src: local("Segoe UI Semibold"), local("SegoeUI-Semibold"); font-weight: 600; }
    #root { position: absolute; inset: 0; pointer-events: none; }
    .cap { position: absolute; left: 160px; right: 160px; bottom: 112px; display: flex; justify-content: center; direction: rtl; }
    .cap span { display: inline-block; max-width: 1500px; padding: 10px 28px 14px; border-radius: 12px; background: rgba(6, 16, 30, 0.82);
      color: #ffffff; font-family: "AmwagUI", "Segoe UI", sans-serif; font-weight: 600; font-size: 38px; line-height: 1.45; text-align: center; }
  </style>
  <!-- Generated by ../narration/build.mjs from the measured narration. -->
  <div id="root" data-composition-id="eg-captions" data-width="1920" data-height="1080" data-layout-allow-caption-zone>
${capEls}
  </div>
  <script>
    window.__timelines = window.__timelines || {};
    window.__timelines["eg-captions"] = gsap.timeline({ paused: true });
  </script>
</template>
`;
fs.writeFileSync(path.join(PROJECT, "compositions", "eg-captions.html"), captions);

// ---------- 6. Records ----------
fs.writeFileSync(path.join(OUT, "subtitles.srt"), "﻿" + srt);
fs.writeFileSync(path.join(OUT, "timing.json"), JSON.stringify({ total: TOTAL, scenes: timeline.map(({ lines, ...s }) => ({ ...s, lines: lines.map(({ words, ...l }) => l) })), anchors: report }, null, 1));
const imp = meta.importance;
const md = [`# Storyboard — AMWAG Printer Setup, Egyptian Arabic tutorial`, ``, `Generated by \`narration/build.mjs\` from \`script.json\`, \`scenes.json\` and the measured voice. Total length **${Math.floor(TOTAL / 60)}:${String(Math.round(TOTAL % 60)).padStart(2, "0")}** (${TOTAL} s).`, ``];
let n = 0;
for (const sc of timeline) {
  md.push(`## Scene: ${sc.id} — ${sc.start.toFixed(1)}–${(sc.start + sc.duration).toFixed(1)} s (${sc.duration.toFixed(1)} s)`, ``);
  const secs = meta.sections.filter((x) => x.scene === sc.id);
  for (const l of sc.lines) {
    const m = meta.lines[l.id] || {};
    const sec = secs.filter((x) => sc.lines.findIndex((y) => y.id === x.from) <= sc.lines.indexOf(l)).pop();
    const next = sc.lines[sc.lines.indexOf(l) + 1];
    md.push(`### ${++n}. ${l.id} — ${imp[m.imp] || ""}`,
      `- **Learning objective:** ${m.goal || ""}`,
      `- **Narration (${l.d.toFixed(1)} s, at ${(sc.start + l.s).toFixed(1)} s):** ${l.text}`,
      `- **On-screen caption:** ${sec ? `${sec.label} — ${sec.title.replace(/\*/g, "")}${sec.help ? ` (${sec.help})` : ""}` : "—"}`,
      `- **Visual focus:** ${m.focus || ""}`, `- **Cursor:** ${m.cursor || "-"}`, `- **Zoom / highlight:** ${m.zoom || "-"}`,
      `- **Slot:** ${(next ? next.s - l.s : sc.duration - l.s).toFixed(1)} s, then ${next ? `${(next.s - l.e).toFixed(2)} s pause` : `scene ends (${sc.id === "outro" ? "end card hold" : "zoom/blur transition"})`}`, ``);
  }
}
fs.writeFileSync(path.join(OUT, "storyboard.md"), md.join("\n"));
// The narration script as a readable document, with the time each line is spoken.
const mmss = (x) => `${Math.floor(x / 60)}:${(x % 60).toFixed(1).padStart(4, "0")}`;
const scriptMd = [`# سكريبت التعليق الصوتي — AMWAG Printer Setup`, ``, `اللهجة: مصرية · الصوت: ${read(path.join(HERE, "tts-config.json")).voice} · المدة: ${mmss(TOTAL)}`, ``];
for (const sc of timeline) {
  const label = script.scenes.find((s) => s.id === sc.id).label;
  scriptMd.push(`## ${label} (${mmss(sc.start)})`, ``);
  for (const l of sc.lines) scriptMd.push(`**${mmss(sc.start + l.s)}** — ${l.text}`, ``);
}
fs.writeFileSync(path.join(OUT, "script.md"), scriptMd.join("\n"));
const guessed = report.filter((x) => x.guessed);
console.log(`timeline ${TOTAL}s, ${subs.length} subtitles, ${report.length} anchors (${guessed.length} estimated: ${guessed.map((x) => x.line + ":" + x.anchor).join(", ") || "none"})`);
