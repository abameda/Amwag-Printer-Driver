// Animation timing rules: turn measured narration into the time of every on-screen event.
// Each scene gets `ctx` with:
//   L(id)          -> { s, e, d } start / end / duration of a narration line, in seconds from the scene start
//   A(id, anchor)  -> the moment the anchor word of that line is spoken
//   dur            -> the scene length
// and returns { cues: { name: seconds }, sfx: [{ t, name, vol }] } in scene time.
// Scenes (composition-eg/compositions/eg-*.html) animate by these cue names only, so retiming the narration retimes
// the picture, the sounds and the captions together.

const r3 = (x) => Math.round(x * 1000) / 1000;
const POP = ["pop-a", "pop-b", "pop-c"];

function kit(ctx) {
  const cues = {};
  const sfx = [];
  let pops = 0;
  const set = (name, t) => (cues[name] = r3(t));
  const pop = (t, vol = 0.4) => sfx.push({ t: r3(t), name: POP[pops++ % 3], vol });
  const click = (t) => sfx.push({ t: r3(t), name: sfx.filter((s) => s.name.startsWith("click")).length % 2 ? "click-b" : "click-a", vol: 0.85 });
  const whoosh = (t, vol = 0.45) => sfx.push({ t: r3(t), name: sfx.filter((s) => s.name.startsWith("slide")).length % 2 ? "slide-b" : "slide-a", vol });
  return { cues, sfx, set, pop, click, whoosh, ...ctx };
}

export const scenes = {
  intro(ctx) {
    const k = kit(ctx);
    const { L, A, set, pop } = k;
    set("subtitle", L("intro.1").s + 0.1);
    set("title", A("intro.1", "title") - 0.15);
    ["find", "driver", "size", "verify"].forEach((a, i) => {
      set(`chip${i + 1}`, A("intro.2", a) - 0.1);
      pop(A("intro.2", a) - 0.08, 0.3);
    });
    set("exit", k.dur - 0.45);
    k.whoosh(k.dur - 0.3, 0.35);
    return k;
  },

  open(ctx) {
    const k = kit(ctx);
    const { L, A, set, click } = k;
    const clickAt = Math.max(A("open.1", "click") + 0.1, L("open.1").s + 2.2);
    set("cursorIn", L("open.1").s + 0.8);
    set("cursorArrive", clickAt - 0.25);
    set("punchIn", clickAt - 1.1);
    set("click1", clickAt);
    set("click2", clickAt + 0.16);
    click(clickAt);
    click(clickAt + 0.16);
    const splash = Math.max(A("open.2", "splash") - 0.1, clickAt + 0.5);
    set("punchOut", splash - 0.2);
    set("splash", splash);
    set("load2", splash + 0.55 * (k.dur - 0.6 - splash));
    set("exit", k.dur - 0.5);
    k.whoosh(k.dur - 0.35, 0.4);
    return k;
  },

  tutorial(ctx) {
    const k = kit(ctx);
    const { L, A, set, pop, click } = k;
    const mid = (id, f) => L(id).s + f * L(id).d;

    // Detection and driver
    set("detectRun", 0.4);
    set("langSpot", A("steps.0", "lang") - 0.15);
    set("langSpotOut", L("steps.0").e + 0.15);
    set("listSpot", A("steps.1", "list") - 0.1);
    set("listSpotOut", L("steps.1").e + 0.1);
    set("greenHint", A("steps.1", "green") - 0.1);
    set("detectDone", A("steps.2", "card") - 0.3); pop(A("steps.2", "card") - 0.3);
    set("cardIn", A("steps.2", "card") - 0.1);
    set("cardSpot", A("steps.2", "card") + 0.1);
    set("cardSpotOut", L("steps.2").e + 0.15);
    set("checkRun", L("steps.3").s + 0.1);
    set("checkDone", mid("steps.3", 0.42)); pop(mid("steps.3", 0.42));
    set("installRun", mid("steps.3", 0.42) + 0.3);
    set("phaseIntegrity", A("steps.3", "install") - 0.4);
    set("phaseInstalling", L("steps.3").e + 0.2);
    set("closeSpot", L("steps.5").s + 0.1);
    set("closeSpotOut", L("steps.5").e + 0.2);

    // Windows permission
    set("noticeIn", A("uac.1", "notice") - 0.25);
    set("noticePunch", A("uac.1", "notice") - 0.05);
    set("noticePunchOut", L("uac.2").s - 0.2);
    const dialog = A("uac.2", "dialog") - 0.1;
    const yes = A("uac.2", "click") + 0.12;
    set("dialogIn", dialog);
    set("cursorYes", dialog + 0.5);
    set("yesArrive", yes - 0.3);
    set("yesSpot", dialog + 0.6);
    set("yesClick", yes); click(yes);
    set("dialogOut", yes + 0.18);
    set("noticeOut", L("uac.3").s + 0.3);

    // Configure, verify, ready
    set("installDone", L("ready.1").s + 0.15); pop(L("ready.1").s + 0.15);
    set("configRun", L("ready.1").s + 0.35);
    set("sizeRow", A("ready.1", "size") - 0.1);
    set("sizeRowOut", L("ready.1").e + 0.3);
    set("configDone", L("ready.1").e - 0.15); pop(L("ready.1").e - 0.15);
    set("verifyRun", A("ready.2", "verify") - 0.15);
    set("verifyDone", L("ready.2").e - 0.1); pop(L("ready.2").e - 0.1);
    set("readyRun", L("ready.2").e + 0.1);
    const ready = A("ready.3", "ready") - 0.2;
    set("readyDone", ready); pop(ready);
    set("resultIn", ready + 0.1);
    k.sfx.push({ t: ready + 0.15, name: "bell", vol: 0.25 });
    set("resultPunch", ready + 0.35);
    set("resultPunchOut", L("ready.4").s - 0.05);
    set("summarySpot", A("ready.4", "summary") - 0.1);
    set("summarySpotOut", L("ready.4").e + 0.2);

    // Test ticket
    set("pttCursor", L("test.1").s + 0.1);
    set("pttSpot", L("test.1").s + 0.4);
    const ptt = L("test.1").e - 0.05;
    set("pttArrive", ptt - 0.35);
    set("pttClick", ptt); click(ptt);
    set("testView", ptt + 0.2);
    const print = L("test.2").e - 0.05;
    set("printSpot", A("test.2", "click") - 0.3);
    set("printArrive", print - 0.3);
    set("printClick", print); click(print);
    set("sending", print + 0.15);
    set("printing", mid("test.3", 0.4));
    set("slotIn", mid("test.3", 0.3));
    set("feed", mid("test.3", 0.45)); k.whoosh(mid("test.3", 0.45), 0.5);
    set("sentView", L("test.3").e - 0.1);
    set("checksPunch", L("test.4").s - 0.15);
    set("corners", A("test.4", "corners")); pop(A("test.4", "corners"));
    set("bars", A("test.5", "bars")); pop(A("test.5", "bars"));
    set("upright", A("test.6", "upright")); pop(A("test.6", "upright"));
    set("tray", A("test.6", "tray")); pop(A("test.6", "tray"));
    set("checksPunchOut", L("test.7").s - 0.1);
    set("okCursor", L("test.7").s);
    const ok = L("test.7").e - 0.05;
    set("okSpot", L("test.7").s + 0.3);
    set("okArrive", ok - 0.3);
    set("okClick", ok); click(ok);
    set("resultBack", ok + 0.2);
    set("ticketOut", ok + 0.1);

    // Ticket software (optional)
    const soft = A("soft.1", "click") + 0.25;
    set("softSpot", L("soft.1").s + 0.1);
    set("softArrive", soft - 0.35);
    set("softClick", soft); click(soft);
    set("softView", soft + 0.2);
    set("jobFound", L("soft.2").s + 0.2);
    const table = A("soft.2", "table") - 0.2;
    set("banner", table);
    set("tablePunch", table + 0.1);
    [0, 1, 2, 3].forEach((i) => { set(`row${i + 1}`, table + 0.25 + i * 0.5); pop(table + 0.25 + i * 0.5, 0.3); });
    set("tablePunchOut", L("soft.3").e - 0.2);

    // Situations
    set("casesReset", L("cases.1").s + 0.2);
    set("chooseView", L("cases.2").s - 0.1);
    set("cardsSpot", A("cases.2", "choose") - 0.2);
    const pick = A("cases.2", "choose") + 0.35;
    set("pickArrive", pick - 0.3);
    set("pickClick", pick); click(pick);
    const cont = A("cases.2", "click") + 0.2;
    set("contSpot", cont - 0.6);
    set("contArrive", cont - 0.3);
    set("contClick", cont); click(cont);
    set("netView", L("cases.3").s - 0.1);
    set("fieldSpot", A("cases.3", "type") - 0.6);
    set("typeStart", A("cases.3", "type") - 0.1);
    set("typeEnd", A("cases.3", "type") + 0.75);
    const find = A("cases.3", "click") + 0.2;
    set("findArrive", find - 0.3);
    set("findClick", find); click(find);
    set("confirmView", L("cases.4").s - 0.1);
    set("labelCard", A("cases.4", "label") - 0.1);
    const yes2 = L("cases.4").e - 0.1;
    set("yes2Arrive", yes2 - 0.35);
    set("yes2Click", yes2); click(yes2);
    set("declinedView", L("cases.5").s - 0.1);
    const retry = A("cases.5", "click") + 0.2;
    set("retrySpot", retry - 0.7);
    set("retryArrive", retry - 0.3);
    set("retryClick", retry); click(retry);
    set("adminIn", L("cases.6").s + 0.15);
    set("adminOut", L("cases.6").e + 0.25);

    // Problems
    set("fixSent", L("fix.1").s - 0.1);
    const problem = A("fix.1", "click") + 0.35;
    set("problemArrive", problem - 0.3);
    set("problemClick", problem); click(problem);
    set("guidanceView", problem + 0.2);
    set("guideSpot", A("fix.1", "list") - 0.1);
    set("statusView", L("fix.2").s - 0.1);
    set("problemSpot", A("fix.2", "problem") - 0.2);
    set("notReadyView", L("fix.3").s - 0.1);
    set("repairSpot", A("fix.3", "repair") - 0.15);
    const copy = A("fix.4", "click") + 0.3;
    set("copySpot", copy - 0.7);
    set("copyArrive", copy - 0.3);
    set("copyClick", copy); click(copy);
    set("copied", copy + 0.15);
    set("exit", k.dur - 0.5);
    k.whoosh(k.dur - 0.4, 0.4);
    return k;
  },

  outro(ctx) {
    const k = kit(ctx);
    const { L, A, set, pop } = k;
    set("logo", 0.1);
    ["s1", "s2", "s3", "s4"].forEach((a, i) => { set(`recap${i + 1}`, A("outro.1", a) - 0.1); pop(A("outro.1", a) - 0.1, 0.3); });
    set("credit", A("outro.2", "credit") - 0.1);
    k.whoosh(A("outro.2", "credit") - 0.05, 0.3);
    return k;
  },
};
