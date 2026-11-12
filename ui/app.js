// Lumina — логика интерфейса. Связь с программой через window.chrome.webview:
//   запрос {callId, cmd, ...} → ответ {id: callId, ok, data | error}; события без id: {event: "progress"|"stage"|"log"|"game"|"auth", ...}.
// ДИЗАЙН сюда не относится — внешний вид меняется в style.css и index.html (см. DESIGN-BRIEF.md).
"use strict";

const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];

// ---------- связь с программой ----------
const host = window.chrome && window.chrome.webview;
let callId = 0;
const waiting = new Map();
if (host) host.addEventListener("message", (e) => {
  const r = e.data;
  if (r && r.event) return onEvent(r);
  const w = waiting.get(r.id);
  if (!w) return;
  waiting.delete(r.id);
  r.ok ? w.resolve(r.data) : w.reject(new Error(r.error || "ошибка"));
});
function call(cmd, args = {}) {
  if (!host) return Promise.reject(new Error("Откройте через Lumina.exe"));
  const id = ++callId;
  return new Promise((resolve, reject) => {
    waiting.set(id, { resolve, reject });
    // callId отдельно от аргументов: многие команды передают id инсталляции в поле id.
    host.postMessage({ ...args, cmd, callId: id });
  });
}
// То же, но с файлами (перетаскивание): программа получает их настоящие пути в поле files.
function callWithFiles(cmd, args, files) {
  if (!host || !host.postMessageWithAdditionalObjects) return Promise.reject(new Error("Перетаскивание файлов недоступно"));
  const id = ++callId;
  return new Promise((resolve, reject) => {
    waiting.set(id, { resolve, reject });
    host.postMessageWithAdditionalObjects({ ...args, cmd, callId: id }, files);
  });
}

// ---------- состояние ----------
const state = { settings: {}, instances: [], accounts: [], system: {}, azure: false, running: null, busy: false, versions: null };

async function refresh() {
  const d = await call("init");
  state.settings = d.settings; state.instances = d.instances; state.accounts = d.accounts;
  state.system = d.system; state.azure = d.azureConfigured; state.running = d.running; state.busy = d.busy;
  state.pack = d.optimizationPack || [];
  state.packs = d.optimizationPacks || {};
  $("#app-version").textContent = `Lumina ${d.appVersion} · файлы игры: ${d.paths.data}`;
  renderInstances(); renderAccounts(); renderSettings(); renderMods();
  setRunning(!!d.running);
}

// ---------- экраны ----------
function showScreen(name) {
  $$(".nav-item[data-screen]").forEach((b) => b.classList.toggle("active", b.dataset.screen === name));
  $$(".screen").forEach((s) => s.classList.toggle("active", s.id === "screen-" + name));
  document.body.dataset.screen = name;
  if (name === "mods") renderMods();
  if (name === "log") scrollLog(true);
  if (name === "skins") skinsOpen();
  if (name === "servers") srvOpen();
  if (name === "worlds") wrLoad();
  if (name === "settings") gameModeHint();
}
$$("[data-screen]").forEach((b) => b.addEventListener("click", () => showScreen(b.dataset.screen)));

// ---------- инсталляции ----------
const LOADER_TITLES = { vanilla: "Vanilla", fabric: "Fabric", quilt: "Quilt", forge: "Forge", neoforge: "NeoForge" };
const loaderName = (i) => (LOADER_TITLES[i.loader] || "Vanilla") + (i.loader !== "vanilla" && i.loaderVersion ? " " + i.loaderVersion : "");
const selected = () => state.instances.find((i) => i.id === $("#instance-select").value);

function renderInstances() {
  const list = $("#instance-list"), tpl = $("#instance-template");
  list.innerHTML = "";
  for (const i of state.instances) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.id = i.id;
    setIcon($(".inst-block-icon", n), iconSrc(i));
    $(".inst-block-icon", n).alt = "";
    $(".inst-name", n).textContent = i.name;
    $(".inst-version", n).textContent = i.mc;
    $(".inst-loader", n).textContent = loaderName(i) + (i.optimized ? " · оптимизация" : "");
    $(".inst-played", n).textContent = i.lastPlayed ? "Играли " + new Date(i.lastPlayed).toLocaleString("ru") : "Ещё не запускалась";
    n.classList.toggle("running", state.running === i.id);
    list.appendChild(n);
  }
  $("#instances-empty").hidden = state.instances.length > 0;

  for (const sel of [$("#instance-select"), $("#mods-instance")]) {
    const keep = sel.value || state.settings.lastInstance;
    sel.innerHTML = "";
    for (const i of state.instances) sel.add(new Option(`${i.name} — ${i.mc} ${loaderName(i)}`, i.id));
    if (!state.instances.length) sel.add(new Option("Нет инсталляций — создайте", ""));
    if (state.instances.some((i) => i.id === keep)) sel.value = keep;
  }
  fillInstanceSelect($("#srv-instance"), "");
  fillInstanceSelect($("#wr-instance"), "Все инсталляции");
  updatePlayInfo();
}

function updatePlayInfo() {
  const i = selected();
  $("#instance-info").textContent = i ? `${i.name} · ${i.mc} · ${loaderName(i)}` : "Создайте инсталляцию, чтобы начать";
  $("#play-btn").disabled = !i || state.busy;
}

$("#instance-select").addEventListener("change", () => { updatePlayInfo(); call("selectInstance", { id: $("#instance-select").value }).catch(() => {}); });

$("#instance-list").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-action]"), card = e.target.closest(".instance-card");
  if (!b || !card) return;
  const inst = state.instances.find((i) => i.id === card.dataset.id);
  switch (b.dataset.action) {
    case "play": $("#instance-select").value = inst.id; updatePlayInfo(); showScreen("play"); play(); break;
    case "edit": openEditor(inst); break;
    case "graphics": openGfx(inst); break;
    case "folder": call("openFolder", { what: "instance", id: inst.id }); break;
    case "delete":
      if (!confirm(`Удалить «${inst.name}»? Папка (с мирами) уйдёт в корзину Windows.`)) return;
      try { state.instances = await call("deleteInstance", { id: inst.id }); renderInstances(); toast("Удалено в корзину", "ok"); }
      catch (err) { toast(err.message, "error"); }
  }
});

// ---------- редактор инсталляции ----------
let editing = null;
async function loadVersions() {
  const sel = $("#ie-version");
  sel.innerHTML = ""; sel.add(new Option("Загрузка…", ""));
  try {
    const list = await call("getVersions");   // только релизы, от новых до 1.20
    sel.innerHTML = "";
    for (const v of list) sel.add(new Option(v.id, v.id));
  } catch (e) { sel.innerHTML = ""; sel.add(new Option("Нет связи: " + e.message, "")); }
}

// Какие загрузчики есть для выбранной версии игры: недоступные помечаются «нет для этой версии».
let loaderSeq = 0, supportSeq = 0;
async function loadLoaderSupport() {
  const mc = $("#ie-version").value, seq = ++supportSeq;
  const opts = [...$("#ie-loader").options];
  for (const o of opts) { o.disabled = false; o.textContent = o.dataset.title || (o.dataset.title = o.textContent); }
  if (!mc) return;
  try {
    const sup = await call("getLoaderSupport", { mc });
    if (seq !== supportSeq) return;
    for (const o of opts) if (o.value !== "vanilla" && !sup[o.value]) { o.disabled = true; o.textContent = o.dataset.title + " — нет для этой версии"; }
  } catch (e) { /* без сети: оставляем как есть, ошибка будет при запуске */ }
}

async function loadLoaders() {
  const wrap = $("#ie-loader-version-wrap"), sel = $("#ie-loader-version"), loader = $("#ie-loader").value;
  const opt = $("#ie-loader").selectedOptions[0];
  $("#ie-save").disabled = !!(opt && opt.disabled);
  wrap.hidden = loader === "vanilla";
  if (wrap.hidden) return;
  const seq = ++loaderSeq;
  sel.innerHTML = ""; sel.add(new Option("Загрузка…", ""));
  try {
    const list = await call("getLoaderVersions", { loader, mc: $("#ie-version").value });
    if (seq !== loaderSeq) return;
    sel.innerHTML = "";
    if (!list.length) { sel.add(new Option("нет для этой версии", "")); $("#ie-save").disabled = true; return; }
    const rec = list.find((l) => l.recommended);
    sel.add(new Option("Рекомендуемая" + (rec ? " (" + rec.version + ")" : ""), ""));
    for (const l of list) sel.add(new Option(l.version + (l.stable ? "" : " (beta)"), l.version));
    if (editing && editing.loader === loader && editing.loaderVersion) sel.value = editing.loaderVersion;
  } catch (e) { if (seq === loaderSeq) { sel.innerHTML = ""; sel.add(new Option("Нет связи: " + e.message, "")); } }
}

async function refreshLoaders() { await loadLoaderSupport(); await loadLoaders(); }

// ---------- иконки сборок ----------
const INSTANCE_ICONS = [
  ["grass", "Трава"], ["stone", "Камень"], ["diamond", "Алмаз"], ["emerald", "Изумруд"], ["gold", "Золото"],
  ["redstone", "Редстоун"], ["tnt", "Динамит"], ["pickaxe", "Кирка"], ["sword", "Меч"], ["chest", "Сундук"],
  ["furnace", "Печь"], ["crafting", "Верстак"], ["book", "Книга"], ["bed", "Кровать"], ["nether", "Незер"], ["end", "Энд"],
];
const LOADER_ICON = { vanilla: "grass", fabric: "crafting", quilt: "book", forge: "furnace", neoforge: "redstone" };
function iconKey(i) { return i && i.icon && i.icon !== "custom" ? i.icon : (i && LOADER_ICON[i.loader]) || "grass"; }
function iconSrc(i) { return i && i.icon === "custom" && i.iconData ? i.iconData : `img/instance-icons/${iconKey(i)}.svg`; }
// Картинки нет (Gemini ещё не нарисовал) — показать блок травы
function setIcon(img, src) { img.onerror = () => { img.onerror = null; img.src = "img/cube-grass.svg"; }; img.src = src; }

let editIcon = "";
function renderIconPicker() {
  const box = $("#ie-icons");
  box.innerHTML = "";
  const custom = editing && editing.icon === "custom" && editing.iconData;
  for (const [key, title] of INSTANCE_ICONS) {
    const b = document.createElement("button");
    b.type = "button"; b.className = "icon-choice"; b.dataset.icon = key; b.title = title;
    b.classList.toggle("active", editIcon ? editIcon === key : !custom && key === iconKey({ loader: $("#ie-loader").value }));
    const img = document.createElement("img"); img.alt = title; setIcon(img, `img/instance-icons/${key}.svg`);
    b.appendChild(img); box.appendChild(b);
  }
  if (custom) {
    const b = document.createElement("button");
    b.type = "button"; b.className = "icon-choice"; b.dataset.icon = "custom"; b.title = "Своя картинка";
    b.classList.toggle("active", !editIcon || editIcon === "custom");
    const img = document.createElement("img"); img.alt = "Своя"; img.src = editing.iconData;
    b.appendChild(img); box.appendChild(b);
  }
}
$("#ie-icons").addEventListener("click", (e) => {
  const b = e.target.closest(".icon-choice");
  if (!b) return;
  editIcon = b.dataset.icon;
  $$("#ie-icons .icon-choice").forEach((x) => x.classList.toggle("active", x === b));
});
$("#ie-icon-custom").addEventListener("click", async () => {
  if (!editing) { toast("Сначала сохраните сборку, потом выберите свою картинку", "error"); return; }
  try {
    const r = await call("pickInstanceIcon", { id: editing.id });
    if (!r) return;
    editing = r; editIcon = "custom";
    renderIconPicker(); await refresh();
    toast("Иконка обновлена", "ok");
  } catch (err) { toast(err.message, "error"); }
});

async function openEditor(inst) {
  editing = inst || null;
  editIcon = inst && inst.icon ? inst.icon : "";
  $("#ie-title").textContent = inst ? "Изменить инсталляцию" : "Новая инсталляция";
  $("#ie-name").value = inst ? inst.name : "";
  $("#ie-loader").value = inst ? inst.loader : "fabric";
  $("#ie-ram").value = inst ? inst.ramMb : 0;
  $("#ie-jvm").value = inst ? inst.jvmArgs : "";
  renderIconPicker();
  openModal("#instance-editor");
  await loadVersions();
  if (inst) {
    if (![...$("#ie-version").options].some((o) => o.value === inst.mc)) $("#ie-version").add(new Option(inst.mc, inst.mc), 0);
    $("#ie-version").value = inst.mc;
  }
  refreshLoaders();
}
$("#new-instance-btn").addEventListener("click", () => openEditor(null));
$("#ie-version").addEventListener("change", refreshLoaders);
$("#ie-loader").addEventListener("change", () => { loadLoaders(); if (!editIcon) renderIconPicker(); });
$("#ie-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const inst = {
    id: editing ? editing.id : "", name: $("#ie-name").value, mc: $("#ie-version").value,
    loader: $("#ie-loader").value, loaderVersion: $("#ie-loader").value !== "vanilla" ? $("#ie-loader-version").value : "",
    ramMb: parseInt($("#ie-ram").value, 10) || 0, jvmArgs: $("#ie-jvm").value,
    icon: editIcon,
  };
  await busy($("#ie-save"), "Сохраняю…", async () => {
    const saved = await call("saveInstance", { instance: inst });
    closeModals();
    state.settings.lastInstance = saved.id;
    await refresh();
    $("#instance-select").value = saved.id; updatePlayInfo();
    toast("Сохранено", "ok");
  });
});

// ---------- играть ----------
// server — "host:port": сразу зайти на сервер (экран «Серверы»).
async function play(server) {
  if (typeof server !== "string") server = "";
  const i = selected();
  if (!i) return toast("Сначала создайте инсталляцию", "error");
  if (!state.accounts.length) { showScreen("account"); return toast("Войдите в аккаунт Microsoft с купленным Minecraft", "error"); }
  $("#crash-panel").hidden = true;
  setBusy(true);
  setProgress(0, "", "");
  try {
    const r = await call("play", { id: i.id, server });
    toast((server ? `Вход на ${server}. ` : "") + `Игра запущена за ${r.seconds} с · ${r.ramMb} МБ · Java ${r.java}`, "ok");
  } catch (e) {
    status(e.message, "error");
    $("#progress").dataset.state = "error";
  } finally { setBusy(false); }
}
$("#play-btn").addEventListener("click", () => play());
$("#cancel-btn").addEventListener("click", () => call("cancel"));
$("#kill-btn").addEventListener("click", () => { if (confirm("Закрыть игру принудительно? Несохранённое может потеряться.")) call("killGame"); });

function setBusy(on) {
  state.busy = on;
  document.body.classList.toggle("busy", on);
  $("#play-btn").classList.toggle("loading", on);
  $("#play-btn").textContent = on ? "Подготовка…" : state.running ? "Игра запущена" : "Играть";
  $("#cancel-btn").hidden = !on;
  $("#progress").dataset.state = on ? "active" : "idle";
  updatePlayInfo();
}

function setRunning(on) {
  document.body.classList.toggle("game-running", on);
  $("#kill-btn").hidden = !on;
  $("#play-btn").textContent = on ? "Игра запущена" : state.busy ? "Подготовка…" : "Играть";
  $("#play-btn").disabled = on || state.busy || !selected();
  $$(".instance-card").forEach((c) => c.classList.toggle("running", on && c.dataset.id === state.running));
}

function setProgress(frac, label, text) {
  const p = $("#progress");
  p.style.setProperty("--progress", Math.max(0, Math.min(1, frac)).toFixed(4));
  $("#progress-label").textContent = label;
  $("#progress-text").textContent = text;
}

const mb = (b) => (b / 1048576).toFixed(b > 104857600 ? 0 : 1);
function onProgress(d) {
  const frac = d.bytesTotal > 0 ? d.bytes / d.bytesTotal : d.filesTotal ? d.files / d.filesTotal : 0;
  let text = `${d.files} / ${d.filesTotal} файлов`;
  if (d.bytesTotal > 0) text += ` · ${mb(d.bytes)} / ${mb(d.bytesTotal)} МБ`;
  if (d.speed > 0) text += ` · ${mb(d.speed)} МБ/с`;
  setProgress(frac, d.label, text);
}

function status(text, kind = "") {
  const s = $("#play-status");
  s.textContent = text;
  s.dataset.kind = kind;
}

// ---------- события из программы ----------
function onEvent(ev) {
  switch (ev.event) {
    case "progress": onProgress(ev); if (ev.label && ev.label.startsWith("Моды")) $("#mods-summary").textContent = $("#progress-text").textContent; break;
    case "stage":
      status(ev.text, ev.stage === "error" ? "error" : ev.stage === "done" ? "ok" : "loading");
      if (ev.stage === "done") { setProgress(1, "Готово", ""); $("#progress").dataset.state = "done"; }
      if (state.modsBusy && ev.stage === "mods") $("#mods-summary").textContent = ev.text;
      break;
    case "log": logLine(ev.line); break;
    case "game":
      state.running = ev.state === "running" ? ev.instance : null;
      setRunning(ev.state === "running");
      if (ev.state === "exited") {
        status(ev.crashed ? `Игра завершилась с ошибкой (код ${ev.code})` : "Игра закрыта", ev.crashed ? "error" : "");
        if (ev.crashed) {
          $("#crash-code").textContent = `(код ${ev.code})`;
          $("#crash-tail").textContent = (ev.tail || []).slice(-25).join("\n");
          $("#crash-panel").hidden = false;
          showScreen("play");
        }
        refresh().catch(() => {});
      }
      break;
    case "auth": onAuth(ev); break;
  }
}

// ---------- лог ----------
const LOG_MAX = 4000;
let logBuf = [], logScheduled = false;
function logLine(line) {
  logBuf.push(line);
  if (!logScheduled) { logScheduled = true; requestAnimationFrame(flushLog); }
}
function flushLog() {
  logScheduled = false;
  const out = $("#log-output");
  const frag = document.createDocumentFragment();
  for (const l of logBuf) {
    const div = document.createElement("div");
    div.className = "log-line" + (/\b(ERROR|FATAL|Exception)\b/.test(l) ? " error" : /\bWARN\b/.test(l) ? " warn" : "");
    div.textContent = l;
    frag.appendChild(div);
  }
  logBuf = [];
  out.appendChild(frag);
  while (out.childElementCount > LOG_MAX) out.firstElementChild.remove();
  scrollLog(false);
}
function scrollLog(force) {
  const out = $("#log-output");
  if (force || $("#log-autoscroll").checked) out.scrollTop = out.scrollHeight;
}
$("#log-clear").addEventListener("click", () => ($("#log-output").innerHTML = ""));
$("#log-copy").addEventListener("click", () => call("copyText", { text: $("#log-output").innerText }).then(() => toast("Лог скопирован", "ok")));
$("#log-open-folder").addEventListener("click", () => selected() && call("openFolder", { what: "logs", id: selected().id }));
$("#crash-log").addEventListener("click", () => showScreen("log"));
$("#crash-folder").addEventListener("click", () => selected() && call("openFolder", { what: "logs", id: selected().id }));
$$("[data-close-crash]").forEach((b) => b.addEventListener("click", () => ($("#crash-panel").hidden = true)));

// ---------- моды ----------
async function renderMods() {
  const id = $("#mods-instance").value;
  const packList = $("#mods-pack-list");
  packList.innerHTML = "";
  const inst = state.instances.find((i) => i.id === id);
  const pack = inst ? (state.packs[inst.loader] || []) : [];
  for (const s of pack) { const li = document.createElement("li"); li.textContent = s; li.dataset.slug = s; packList.appendChild(li); }
  $("#optimize-btn").disabled = !inst || inst.loader === "vanilla" || state.modsBusy;
  mnSync();
  if (!inst) { $("#mods-summary").textContent = "Нет инсталляции"; $("#mods-list").innerHTML = ""; return; }
  if (state.modsBusy) return;
  $("#mods-summary").textContent = inst.loader === "vanilla"
    ? "Пакету нужен загрузчик модов — выберите Fabric, Quilt, Forge или NeoForge в настройках инсталляции."
    : inst.optimized ? "Установлен. Нажмите, чтобы обновить до свежих версий." : "Sodium, Lithium и другие моды для высокого FPS. Ничего не меняет в игровом процессе.";
  $("#optimize-btn").textContent = inst.optimized ? "Обновить" : "Установить";
  try {
    const st = await call("modsStatus", { id });
    const list = $("#mods-list"), tpl = $("#mod-template");
    list.innerHTML = "";
    const inPack = new Set();
    for (const m of st.mods) {
      const n = tpl.content.firstElementChild.cloneNode(true);
      n.dataset.file = m.file;
      n.classList.toggle("from-pack", m.fromPack);
      n.classList.toggle("disabled", !m.enabled);
      $(".mod-name", n).textContent = m.file.replace(/\.disabled$/, "");
      $(".mod-size", n).textContent = (m.size / 1024).toFixed(0) + " КБ";
      $(".mod-badge", n).hidden = !m.fromPack;
      $(".mod-toggle", n).checked = m.enabled;
      if (m.fromPack) inPack.add(m.slug);
      list.appendChild(n);
    }
    $$("li", packList).forEach((li) => li.classList.toggle("installed", inPack.has(li.dataset.slug)));
    $("#mods-empty").hidden = st.mods.length > 0;
    showSkipped(st.skipped);
  } catch (e) { toast(e.message, "error"); }
}
function showSkipped(skipped) {
  const el = $("#mods-skipped");
  el.hidden = !skipped || !skipped.length;
  if (!el.hidden) el.textContent = "Нет сборки для этой версии, пропущено: " + skipped.join(", ");
  $$("#mods-pack-list li").forEach((li) => li.classList.toggle("skipped", !!skipped && skipped.includes(li.dataset.slug)));
}
$("#mods-instance").addEventListener("change", renderMods);
$("#open-mods-btn").addEventListener("click", () => $("#mods-instance").value && call("openFolder", { what: "mods", id: $("#mods-instance").value }));
$("#mods-list").addEventListener("change", async (e) => {
  const row = e.target.closest(".mod-row");
  if (!row || !e.target.classList.contains("mod-toggle")) return;
  try { await call("toggleMod", { id: $("#mods-instance").value, file: row.dataset.file }); renderMods(); }
  catch (err) { toast(err.message, "error"); e.target.checked = !e.target.checked; }
});
$("#optimize-btn").addEventListener("click", async () => {
  const id = $("#mods-instance").value;
  state.modsBusy = true;
  document.body.classList.add("mods-busy");
  $("#optimize-btn").classList.add("loading");
  $("#optimize-btn").disabled = true;
  try {
    const r = await call("installOptimization", { id });
    toast(`Установлено модов: ${r.installed.length}` + (r.skipped.length ? `, пропущено: ${r.skipped.length}` : ""), "ok");
    state.modsBusy = false;
    await refresh();
  } catch (e) { toast(e.message, "error"); }
  finally {
    state.modsBusy = false;
    document.body.classList.remove("mods-busy");
    $("#optimize-btn").classList.remove("loading");
    renderMods();
  }
});

// ---------- ModNest: каталог Modrinth для инсталляции ----------
const KIND_LABELS = { mod: "мод", resourcepack: "ресурс-пак", shader: "шейдер", datapack: "датапак", modpack: "сборка" };
const KIND_PLURAL = { mod: "моды", resourcepack: "ресурс-паки", shader: "шейдеры", datapack: "датапаки" };
const mn = { kind: "mod", offset: 0, total: 0, seq: 0, installed: [], cats: {}, lastKey: "" };
const mnInst = () => $("#mods-instance").value;
const fmtNum = (n) => (n >= 1e6 ? (n / 1e6).toFixed(1) + " млн" : n >= 1e3 ? Math.round(n / 1e3) + " тыс." : String(n || 0));
const fmtSize = (b) => (b >= 1048576 ? (b / 1048576).toFixed(1) + " МБ" : Math.max(1, Math.round(b / 1024)) + " КБ");
function mnStatus(text, kind = "") { const el = $("#mn-status"); el.textContent = text || ""; el.dataset.kind = kind; }

async function mnLoadCategories() {
  const sel = $("#mn-category");
  let cats = mn.cats[mn.kind];
  if (!cats) { try { cats = mn.cats[mn.kind] = await call("mnCategories", { kind: mn.kind }); } catch (e) { cats = [{ id: "", label: "Все" }]; } }
  sel.innerHTML = "";
  for (const c of cats) sel.add(new Option(c.label, c.id));
}

// Вызывается из renderMods: при смене инсталляции — новый поиск, иначе только список установленного.
function mnSync() {
  const key = mnInst() + "|" + mn.kind;
  if (key !== mn.lastKey) { mn.lastKey = key; mnSearch(); }
  mnRefreshInstalled();
}

async function mnSearch(append = false) {
  const id = mnInst(), inst = state.instances.find((i) => i.id === id);
  if (!append) { mn.offset = 0; $("#mn-results").innerHTML = ""; }
  const seq = ++mn.seq;
  $("#mn-results-empty").hidden = true; $("#mn-more").hidden = true;
  mnShaderHint("", "");
  if (mn.kind !== "modpack" && !inst) return mnStatus("Сначала создайте инсталляцию", "error");
  if (mn.kind === "mod" && inst.loader === "vanilla")
    return mnStatus("В инсталляции без загрузчика моды не работают — выберите Fabric, Quilt, Forge или NeoForge в её настройках.", "error");
  $("#mn-filter-note").textContent = mn.kind === "modpack" ? "Сборка ставится новой инсталляцией со своим загрузчиком"
    : `Для Minecraft ${inst.mc}` + (mn.kind === "mod" ? ` · ${LOADER_TITLES[inst.loader]}` : " (по версии игры)");
  mnStatus("Поиск…", "loading");
  try {
    const r = await call("mnSearch", { id, kind: mn.kind, query: $("#mn-query").value.trim(), index: $("#mn-sort").value,
      category: $("#mn-category").value, offset: mn.offset, limit: 20 });
    if (seq !== mn.seq) return;
    mn.total = r.total; mn.offset += r.hits.length;
    for (const h of r.hits) $("#mn-results").appendChild(mnCard(h));
    $("#mn-results-empty").hidden = $("#mn-results").children.length > 0;
    $("#mn-more").hidden = mn.offset >= mn.total;
    mnStatus(`Найдено: ${fmtNum(r.total)}`, "");
  } catch (e) { if (seq === mn.seq) mnStatus(e.message, "error"); }
}

function mnCard(h) {
  const n = $("#mn-card-template").content.firstElementChild.cloneNode(true);
  n.dataset.project = h.id; n.dataset.slug = h.slug; n.dataset.type = h.type || mn.kind;
  const img = $(".mn-icon", n);
  if (h.icon) img.src = h.icon; else img.hidden = true;
  $(".mn-title", n).textContent = h.title;
  $(".mn-author", n).textContent = h.author ? h.author : "";
  $(".mn-desc", n).textContent = h.description || "";
  $(".mn-downloads", n).textContent = fmtNum(h.downloads) + " загрузок";
  $(".mn-cats", n).textContent = (h.categories || []).slice(0, 3).join(" · ");
  if (n.dataset.type === "modpack") $("[data-action=install]", n).textContent = "Создать инсталляцию";
  mnMarkInstalled(n, h.installed);
  return n;
}
function mnMarkInstalled(n, on) {
  n.classList.toggle("installed", !!on);
  $("[data-action=remove]", n).hidden = !on;
  if (n.dataset.type !== "modpack") $("[data-action=install]", n).textContent = on ? "Переустановить" : "Установить";
}

function mnShaderHint(loader, note) {
  const box = $("#mn-shader-hint");
  box.hidden = !loader && !note;
  $("#mn-shader-loader-btn").hidden = !loader;
  $("#mn-shader-text").textContent = loader
    ? `Шейдерам нужен мод ${loader === "oculus" ? "Oculus" : "Iris"} — без него они не включатся.` : note || "";
}

function mnPickWorld() {
  return new Promise(async (resolve) => {
    let worlds = [];
    try { worlds = await call("mnWorlds", { id: mnInst() }); } catch (e) { /* пусто */ }
    const sel = $("#mn-world");
    sel.innerHTML = "";
    for (const w of worlds) sel.add(new Option(w, w));
    $("#mn-world-empty").hidden = worlds.length > 0;
    $("#mn-world-ok").disabled = !worlds.length;
    const closers = $$("#mn-world-modal [data-close]");
    const done = (v) => { $("#mn-world-ok").onclick = null; closers.forEach((b) => b.removeEventListener("click", cancel)); closeModals(); resolve(v); };
    const cancel = () => done(null);
    closers.forEach((b) => b.addEventListener("click", cancel));
    $("#mn-world-ok").onclick = () => done(sel.value);
    openModal("#mn-world-modal");
  });
}

async function mnInstall(btn, project, kind, world, card) {
  await busy(btn, kind === "modpack" ? "Создаю…" : "Ставлю…", async () => {
    const r = await call("mnInstall", { id: mnInst(), project, kind, world });
    if (kind === "modpack") {
      await refresh();
      toast(`Создана инсталляция «${r.instance.name}» (${r.mc}, ${LOADER_TITLES[r.loader] || r.loader}): файлов ${r.files}`, "ok");
      return;
    }
    const deps = r.installed.filter((x) => x.dependency).length;
    toast(`Установлено: ${r.installed[0].title}` + (deps ? ` + зависимостей: ${deps}` : "") +
      (r.missing.length ? `. Не найдено: ${r.missing.join(", ")}` : ""), r.missing.length ? "error" : "ok");
    if (card) mnMarkInstalled(card, true);
    mnShaderHint(r.needsShaderLoader, r.shaderNote);
    renderMods();
  });
}

async function mnRefreshInstalled() {
  const id = mnInst(), list = $("#mn-installed");
  if (!id) { list.innerHTML = ""; mn.installed = []; $("#mn-installed-empty").hidden = false; return; }
  try { mn.installed = await call("mnInstalled", { id }); } catch (e) { mn.installed = []; }
  const kind = mn.kind === "modpack" ? "mod" : mn.kind;
  const items = mn.installed.filter((x) => x.kind === kind);
  $("#mn-installed-title").textContent = "Установлено: " + KIND_PLURAL[kind];
  list.innerHTML = "";
  for (const it of items) {
    const n = $("#mn-item-template").content.firstElementChild.cloneNode(true);
    n.dataset.path = it.path;
    n.classList.toggle("disabled", !it.enabled);
    n.classList.toggle("from-pack", !!it.projectId);
    $(".mn-item-name", n).textContent = (it.title || it.file.replace(/\.disabled$/, "")) + (it.title && it.version ? " " + it.version : "") + (it.world ? " — мир «" + it.world + "»" : "");
    $(".mn-item-name", n).title = it.file;
    $(".mn-item-kind", n).textContent = it.dependency ? "зависимость" : it.projectId ? "Modrinth" : KIND_LABELS[it.kind];
    $(".mn-item-size", n).textContent = fmtSize(it.size);
    $(".mn-toggle", n).checked = it.enabled;
    list.appendChild(n);
  }
  $("#mn-installed-empty").hidden = items.length > 0;
}

async function mnRemove(path) {
  const it = mn.installed.find((x) => x.path === path);
  if (!confirm(`Удалить «${it ? it.title || it.file : path}»? Файл уйдёт в Корзину Windows.`)) return false;
  try { await call("mnRemove", { id: mnInst(), path }); toast("Удалено в Корзину", "ok"); renderMods(); return true; }
  catch (e) { toast(e.message, "error"); return false; }
}

async function mnImport(files, world) {
  if (!files || !files.length) return;
  mnStatus("Импорт…", "loading");
  try {
    const args = { id: mnInst(), world: world || "" };
    let r = Array.isArray(files) ? await call("mnImport", { ...args, files }) : await callWithFiles("mnImport", args, files);
    if (r.needWorld.length) {
      const w = await mnPickWorld();
      if (w) { const r2 = await call("mnImport", { id: mnInst(), world: w, files: r.needWorld }); r.imported.push(...r2.imported); r.errors.push(...r2.errors); }
    }
    if (r.created) { await refresh(); toast(`Сборка импортирована как новая инсталляция «${r.created.name}»`, "ok"); }
    else if (r.imported.length) toast(`Импортировано: ${r.imported.map((x) => x.file).join(", ")}`, "ok");
    if (r.errors.length) toast("Не импортировано: " + r.errors.join("; "), "error");
    mnStatus("", "");
    renderMods();
  } catch (e) { mnStatus(e.message, "error"); }
}

$$(".mn-kind").forEach((b) => b.addEventListener("click", async () => {
  mn.kind = b.dataset.mnKind;
  $$(".mn-kind").forEach((x) => x.classList.toggle("active", x === b));
  $("#mn-query").placeholder = { mod: "Поиск модов: sodium, карта, мебель…", resourcepack: "Поиск ресурс-паков: faithful, анимации…",
    shader: "Поиск шейдеров: complementary, bsl…", datapack: "Поиск датапаков: terralith, структуры…", modpack: "Поиск сборок: оптимизация, техника…" }[mn.kind];
  await mnLoadCategories();
  mnSync();
}));
let mnTimer = 0;
$("#mn-query").addEventListener("input", () => { clearTimeout(mnTimer); mnTimer = setTimeout(() => mnSearch(), 400); });
$("#mn-query").addEventListener("keydown", (e) => { if (e.key === "Enter") { clearTimeout(mnTimer); mnSearch(); } });
$("#mn-sort").addEventListener("change", () => mnSearch());
$("#mn-category").addEventListener("change", () => mnSearch());
$("#mn-more").addEventListener("click", () => mnSearch(true));

$("#mn-results").addEventListener("click", async (e) => {
  const btn = e.target.closest("button[data-action]");
  if (!btn) return;
  const card = btn.closest(".mn-card"), project = card.dataset.project, kind = card.dataset.type;
  if (btn.dataset.action === "open") return call("openUrl", { url: `https://modrinth.com/${kind}/${card.dataset.slug}` });
  if (btn.dataset.action === "remove") {
    const it = mn.installed.find((x) => x.projectId === project);
    if (!it) return toast("Файл не найден в инсталляции", "error");
    if (await mnRemove(it.path)) mnMarkInstalled(card, false);
    return;
  }
  let world = "";
  if (kind === "datapack") { world = await mnPickWorld(); if (!world) return; }
  await mnInstall(btn, project, kind, world, card);
});

$("#mn-shader-loader-btn").addEventListener("click", () => busy($("#mn-shader-loader-btn"), "Ставлю…", async () => {
  const r = await call("mnShaderLoader", { id: mnInst() });
  toast(`Установлено: ${r.installed.map((x) => x.title).join(", ")}`, "ok");
  mnShaderHint("", "");
  renderMods();
}));

$("#mn-installed").addEventListener("change", async (e) => {
  const row = e.target.closest(".mn-item");
  if (!row || !e.target.classList.contains("mn-toggle")) return;
  try { await call("mnToggle", { id: mnInst(), path: row.dataset.path }); renderMods(); }
  catch (err) { toast(err.message, "error"); e.target.checked = !e.target.checked; }
});
$("#mn-installed").addEventListener("click", (e) => {
  const b = e.target.closest("[data-action=remove]");
  if (b) mnRemove(b.closest(".mn-item").dataset.path);
});

$("#mn-updates-btn").addEventListener("click", () => {
  if (!mnInst()) return toast("Нет инсталляции", "error");
  busy($("#mn-updates-btn"), "Проверяю…", async () => {
    const r = await call("mnCheckUpdates", { id: mnInst() });
    const ul = $("#mn-updates-list");
    ul.innerHTML = "";
    for (const u of r.updates) {
      const li = document.createElement("li");
      li.textContent = `${u.title || u.file}: ${u.from || "?"} → ${u.to}`;
      ul.appendChild(li);
    }
    $("#mn-updates-title").textContent = r.updates.length ? `Доступно обновлений: ${r.updates.length}`
      : `Всё актуально (проверено файлов: ${r.checked}${r.unknown ? `, не с Modrinth: ${r.unknown}` : ""})`;
    $("#mn-update-all").hidden = !r.updates.length;
    $("#mn-updates").hidden = false;
  });
});
$("#mn-update-all").addEventListener("click", () => busy($("#mn-update-all"), "Обновляю…", async () => {
  const r = await call("mnUpdateAll", { id: mnInst() });
  toast(`Обновлено: ${r.updated.length}`, "ok");
  $("#mn-updates").hidden = true;
  renderMods();
}));

$("#mn-import-btn").addEventListener("click", async () => {
  try { mnImport(await call("mnPickFiles")); } catch (e) { toast(e.message, "error"); }
});
// Перетаскивание файлов на экран «Моды» (без этого WebView2 открыл бы файл вместо страницы).
window.addEventListener("dragover", (e) => {
  e.preventDefault();
  if (document.body.dataset.screen === "mods") $("#mn-drop").hidden = false;
});
window.addEventListener("dragleave", (e) => { if (!e.relatedTarget) $("#mn-drop").hidden = true; });
window.addEventListener("drop", (e) => {
  e.preventDefault();
  $("#mn-drop").hidden = true;
  if (document.body.dataset.screen !== "mods" || !e.dataTransfer || !e.dataTransfer.files.length) return;
  mnImport(e.dataTransfer.files);
});
mnLoadCategories();

// ---------- аккаунт ----------
function renderAccounts() {
  const list = $("#account-list"), tpl = $("#account-template");
  list.innerHTML = "";
  for (const a of state.accounts) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.uuid = a.uuid;
    n.classList.toggle("active", a.selected);
    $(".acc-name", n).textContent = a.name;
    $(".acc-uuid", n).textContent = a.ownsGame ? "Minecraft: Java Edition ✓" : "Нет игры";
    $(".account-avatar", n).textContent = (a.name || "?")[0].toUpperCase();
    list.appendChild(n);
  }
  $("#accounts-empty").hidden = state.accounts.length > 0;
  $("#azure-missing").hidden = state.azure;
  $("#login-btn").classList.toggle("needs-setup", !state.azure);
  const cur = state.accounts.find((a) => a.selected) || state.accounts[0];
  $("#account-chip-name").textContent = cur ? cur.name : "Войти";
  $("#account-chip .account-avatar").textContent = cur ? cur.name[0].toUpperCase() : "";
  document.body.classList.toggle("no-account", !cur);
}
$("#account-list").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-action]"), card = e.target.closest(".account-card");
  if (!b || !card) return;
  try {
    if (b.dataset.action === "logout" && !confirm("Выйти из аккаунта? Токены будут удалены с этого ПК.")) return;
    state.accounts = await call(b.dataset.action === "logout" ? "logout" : "selectAccount", { uuid: card.dataset.uuid });
    await refresh();
  } catch (err) { toast(err.message, "error"); }
});

let loginUrl = "";
$("#login-btn").addEventListener("click", () => {
  if (!state.azure) {
    // без ID приложения Azure вход невозможен — объяснить и показать инструкцию
    $("#azure-missing").hidden = false;
    $("#azure-missing").scrollIntoView({ behavior: "smooth", block: "center" });
    toast("Сначала укажите ID приложения Microsoft в «Настройках» — открываю инструкцию", "error");
    showGuide();
    return;
  }
  startLogin();
});
const startLogin = () => busy($("#login-btn"), "Получаю код…", async () => {
  const r = await call("loginStart");
  loginUrl = r.verificationUri;
  $("#login-code").textContent = r.userCode;
  $("#login-url").textContent = r.verificationUri;
  $("#login-status").textContent = "Ждём подтверждения на сайте Microsoft…";
  $("#login-status").dataset.kind = "loading";
  $("#login-panel").hidden = false;
  document.body.classList.add("logging-in");
});
$("#login-copy").addEventListener("click", () => call("copyText", { text: $("#login-code").textContent }).then(() => toast("Код скопирован", "ok")));
$("#login-open").addEventListener("click", () => call("openUrl", { url: loginUrl }));
$("#login-cancel").addEventListener("click", () => { call("loginCancel"); endLogin(); });
function endLogin() { $("#login-panel").hidden = true; document.body.classList.remove("logging-in"); }
function onAuth(ev) {
  if (ev.state === "progress") { $("#login-status").textContent = ev.text; return; }
  if (ev.state === "ok") { endLogin(); toast(`Вы вошли как ${ev.name}`, "ok"); refresh(); return; }
  $("#login-status").textContent = ev.text;
  $("#login-status").dataset.kind = "error";
  toast(ev.text, "error");
}

// ---------- настройки ----------
function renderSettings() {
  for (const el of $$("[data-setting]")) {
    const v = state.settings[el.dataset.setting];
    if (el.type === "checkbox") el.checked = !!v; else el.value = v ?? "";
  }
  $("#ram-auto-hint").textContent = `ОЗУ компьютера: ${state.system.totalRamMb} МБ. Авто — ${state.system.autoRamMb} МБ.`;
}
function readSettings() {
  const s = { ...state.settings };
  for (const el of $$("[data-setting]")) {
    const k = el.dataset.setting;
    s[k] = el.type === "checkbox" ? el.checked : el.type === "number" ? (parseInt(el.value, 10) || 0) : el.value.trim();
  }
  return s;
}
$("#settings-save").addEventListener("click", () => busy($("#settings-save"), "Сохраняю…", async () => {
  await call("saveSettings", { settings: readSettings() });
  await refresh();
  toast("Настройки сохранены", "ok");
}));
$("#pick-java").addEventListener("click", async () => { const p = await call("pickJava"); if (p) $('[data-setting="javaPath"]').value = p; });
$("#pick-data-dir").addEventListener("click", async () => { const p = await call("pickFolder"); if (p) $('[data-setting="dataDir"]').value = p; });
$("#open-data-dir").addEventListener("click", () => call("openFolder", { what: "data" }));
$("#open-config-dir").addEventListener("click", () => call("openFolder", { what: "config" }));
async function showGuide() { $("#guide-text").textContent = await call("getGuide"); openModal("#guide-modal"); }
$("#guide-btn").addEventListener("click", showGuide);
$("#azure-guide-btn").addEventListener("click", showGuide);
$("#azure-go-settings").addEventListener("click", () => { showScreen("settings"); $('[data-setting="azureClientId"]').focus(); });

// ---------- окна и уведомления ----------
function openModal(sel) { $(sel).hidden = false; document.body.classList.add("modal-open"); }
function closeModals() { $$(".modal").forEach((m) => (m.hidden = true)); document.body.classList.remove("modal-open"); }
$$(".modal [data-close]").forEach((b) => b.addEventListener("click", closeModals));
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeModals(); });

async function busy(btn, text, fn) {
  const old = btn.textContent;
  btn.disabled = true; btn.classList.add("loading"); btn.textContent = text;
  try { await fn(); } catch (e) { toast(e.message, "error"); }
  finally { btn.disabled = false; btn.classList.remove("loading"); btn.textContent = old; }
}

let toastTimer = 0;
function toast(text, kind = "") {
  const t = $("#toast");
  t.textContent = text;
  t.dataset.kind = kind;
  t.hidden = false;
  requestAnimationFrame(() => t.classList.add("show"));
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.classList.remove("show"); setTimeout(() => (t.hidden = true), 300); }, kind === "error" ? 6000 : 3000);
}

// =====================================================================================
// Новые экраны: Скины, Серверы, Миры, автонастройка графики, игровой режим.
// Внешний вид — style.css (раздел «Новые экраны» в конце) и DESIGN-BRIEF.md.
// =====================================================================================
const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString("ru", { dateStyle: "medium", timeStyle: "short" }) : "");

// Список инсталляций в select; allLabel — пункт «все» (значение "").
function fillInstanceSelect(sel, allLabel) {
  if (!sel) return;
  const keep = sel.value || (allLabel ? "" : state.settings.lastInstance);
  sel.innerHTML = "";
  if (allLabel) sel.add(new Option(allLabel, ""));
  for (const i of state.instances) sel.add(new Option(`${i.name} — ${i.mc} ${loaderName(i)}`, i.id));
  if (!allLabel && !state.instances.length) sel.add(new Option("Нет инсталляций", ""));
  if ([...sel.options].some((o) => o.value === keep)) sel.value = keep;
}

function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("Не удалось прочитать картинку"));
    img.src = src;
  });
}

// ---------- Скины: геометрия и развёртка ----------
// Части модели: uv — базовый слой, ov — второй слой (одежда), s — размер [ш, в, г], p — угол (мин. x, y, z) в пикселях модели.
// Модель смотрит на +z; правая рука/нога персонажа — на −x (слева для зрителя).
function skinParts(slim) {
  const aw = slim ? 3 : 4;
  return [
    { n: "head", uv: [0, 0], ov: [32, 0], s: [8, 8, 8], p: [-4, 24, -4], inflate: 0.5 },
    { n: "body", uv: [16, 16], ov: [16, 32], s: [8, 12, 4], p: [-4, 12, -2], inflate: 0.25 },
    { n: "armR", uv: [40, 16], ov: [40, 32], s: [aw, 12, 4], p: [-4 - aw, 12, -2], inflate: 0.25 },
    { n: "armL", uv: [32, 48], ov: [48, 48], s: [aw, 12, 4], p: [4, 12, -2], inflate: 0.25 },
    { n: "legR", uv: [0, 16], ov: [0, 32], s: [4, 12, 4], p: [-4, 0, -2], inflate: 0.25 },
    { n: "legL", uv: [16, 48], ov: [0, 48], s: [4, 12, 4], p: [0, 0, -2], inflate: 0.25 },
  ];
}
// Прямоугольники граней на текстуре: [u, v, ширина, высота].
function faceRects(u, v, w, h, d) {
  return {
    top: [u + d, v, w, d], bottom: [u + d + w, v, w, d],
    right: [u, v + d, d, h], front: [u + d, v + d, w, h], left: [u + d + w, v + d, d, h], back: [u + d + w + d, v + d, w, h],
  };
}

// Старый формат 64×32 → 64×32+низ (как в игре: левые рука и нога — зеркальные копии правых).
function normalizeSkin(img) {
  const c = document.createElement("canvas");
  c.width = 64; c.height = 64;
  const g = c.getContext("2d");
  g.imageSmoothingEnabled = false;
  g.drawImage(img, 0, 0);
  if (img.height === 32) {
    const copies = [[4, 16, 16, 32, 4, 4], [8, 16, 16, 32, 4, 4], [0, 20, 24, 32, 4, 12], [4, 20, 16, 32, 4, 12], [8, 20, 8, 32, 4, 12], [12, 20, 16, 32, 4, 12],
      [44, 16, -8, 32, 4, 4], [48, 16, -8, 32, 4, 4], [40, 20, 0, 32, 4, 12], [44, 20, -8, 32, 4, 12], [48, 20, -16, 32, 4, 12], [52, 20, -8, 32, 4, 12]];
    for (const [sx, sy, dx, dy, w, h] of copies) {
      g.save();
      g.translate(sx + dx + w, sy + dy);
      g.scale(-1, 1);
      g.drawImage(img, sx, sy, w, h, 0, 0, w, h);
      g.restore();
    }
  }
  return c;
}
async function skinCanvas(dataUrl) { return normalizeSkin(await loadImage(dataUrl)); }

// Плоская фигура 16×32 «пикселя скина» (спереди или сзади), с наложением второго слоя.
function drawFigure(g, src, slim, back, ox, oy, k) {
  const aw = slim ? 3 : 4;
  const P = {};
  for (const p of skinParts(slim)) P[p.n] = p;
  const face = back ? "back" : "front";
  const order = back
    ? [["armL", 4 - aw, 8], ["body", 4, 8], ["armR", 12, 8], ["legL", 4, 20], ["legR", 8, 20], ["head", 4, 0]]
    : [["armR", 4 - aw, 8], ["body", 4, 8], ["armL", 12, 8], ["legR", 4, 20], ["legL", 8, 20], ["head", 4, 0]];
  for (const layer of [0, 1]) {
    for (const [n, x, y] of order) {
      const part = P[n], base = layer ? part.ov : part.uv;
      const r = faceRects(base[0], base[1], part.s[0], part.s[1], part.s[2])[face];
      g.drawImage(src, r[0], r[1], r[2], r[3], ox + x * k, oy + y * k, r[2] * k, r[3] * k);
    }
  }
}
function drawSkin2D(canvas, src, slim, both) {
  const g = canvas.getContext("2d");
  g.clearRect(0, 0, canvas.width, canvas.height);
  if (!src) return;
  g.imageSmoothingEnabled = false;
  const views = both ? [false, true] : [false];
  const gap = both ? 5 : 0;
  const totalW = 16 * views.length + gap * (views.length - 1);
  const k = Math.max(1, Math.floor(Math.min(canvas.width / totalW, canvas.height / 32)));
  const ox = Math.floor((canvas.width - totalW * k) / 2), oy = Math.floor((canvas.height - 32 * k) / 2);
  views.forEach((back, i) => drawFigure(g, src, slim, back, ox + i * (16 + gap) * k, oy, k));
}
function drawCape(canvas, img) {
  const g = canvas.getContext("2d");
  g.clearRect(0, 0, canvas.width, canvas.height);
  g.imageSmoothingEnabled = false;
  const f = img.width / 64;   // HD-плащи кратны 64
  g.drawImage(img, 1 * f, 1 * f, 10 * f, 16 * f, 0, 0, canvas.width, canvas.height);
}

// ---------- Скины: 3D-предпросмотр на чистом WebGL ----------
const Skin3D = (() => {
  let gl = null, prog = null, buf = null, tex = null, cv = null, count = 0;
  let angle = 0.55, tilt = 0.12, dragX = null, raf = 0, lastT = 0, idleUntil = 0;
  const VS = "attribute vec3 p;attribute vec2 t;attribute float l;uniform mat4 m;varying vec2 vt;varying float vl;" +
    "void main(){vt=t;vl=l;gl_Position=m*vec4(p,1.0);}";
  const FS = "precision mediump float;uniform sampler2D s;varying vec2 vt;varying float vl;" +
    "void main(){vec4 c=texture2D(s,vt);if(c.a<0.5)discard;gl_FragColor=vec4(c.rgb*vl,1.0);}";
  const M = {
    mul(a, b) {
      const o = new Float32Array(16);
      for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) { let x = 0; for (let k = 0; k < 4; k++) x += a[k * 4 + r] * b[c * 4 + k]; o[c * 4 + r] = x; }
      return o;
    },
    persp(fov, asp, n, f) { const t = 1 / Math.tan(fov / 2); return new Float32Array([t / asp, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, (2 * f * n) / (n - f), 0]); },
    tr(x, y, z) { return new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1]); },
    ry(a) { const c = Math.cos(a), s = Math.sin(a); return new Float32Array([c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]); },
    rx(a) { const c = Math.cos(a), s = Math.sin(a); return new Float32Array([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]); },
  };

  function init(canvas) {
    cv = canvas;
    try { gl = canvas.getContext("webgl", { alpha: true, antialias: true, premultipliedAlpha: false }) || canvas.getContext("experimental-webgl"); }
    catch (e) { gl = null; }
    if (!gl) return false;
    try {
      const sh = (type, src) => {
        const x = gl.createShader(type);
        gl.shaderSource(x, src); gl.compileShader(x);
        if (!gl.getShaderParameter(x, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(x) || "shader");
        return x;
      };
      prog = gl.createProgram();
      gl.attachShader(prog, sh(gl.VERTEX_SHADER, VS));
      gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, FS));
      gl.linkProgram(prog);
      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error("link");
    } catch (e) { gl = null; return false; }
    buf = gl.createBuffer();
    tex = gl.createTexture();
    canvas.addEventListener("pointerdown", (e) => { dragX = e.clientX; try { canvas.setPointerCapture(e.pointerId); } catch (x) { /* нет */ } });
    canvas.addEventListener("pointermove", (e) => {
      if (dragX === null) return;
      angle += (e.clientX - dragX) * 0.012;
      dragX = e.clientX;
      idleUntil = performance.now() + 2500;
      start();
    });
    const up = () => { dragX = null; };
    canvas.addEventListener("pointerup", up);
    canvas.addEventListener("pointercancel", up);
    return true;
  }

  function geometry(slim) {
    const v = [];
    const shade = { top: 1.0, bottom: 0.55, front: 0.93, back: 0.72, right: 0.8, left: 0.82 };
    const box = (x0, y0, z0, x1, y1, z1, u, vv, w, h, d) => {
      const R = faceRects(u, vv, w, h, d);
      const C = {
        front: [[x0, y1, z1], [x1, y1, z1], [x1, y0, z1], [x0, y0, z1]],
        back: [[x1, y1, z0], [x0, y1, z0], [x0, y0, z0], [x1, y0, z0]],
        right: [[x0, y1, z0], [x0, y1, z1], [x0, y0, z1], [x0, y0, z0]],
        left: [[x1, y1, z1], [x1, y1, z0], [x1, y0, z0], [x1, y0, z1]],
        top: [[x0, y1, z0], [x1, y1, z0], [x1, y1, z1], [x0, y1, z1]],
        bottom: [[x0, y0, z1], [x1, y0, z1], [x1, y0, z0], [x0, y0, z0]],
      };
      for (const f of Object.keys(C)) {
        const [ru, rv, rw, rh] = R[f], c = C[f];
        const uv = [[ru, rv], [ru + rw, rv], [ru + rw, rv + rh], [ru, rv + rh]];
        for (const i of [0, 1, 2, 0, 2, 3]) v.push(c[i][0], c[i][1], c[i][2], uv[i][0] / 64, uv[i][1] / 64, shade[f]);
      }
    };
    for (const p of skinParts(slim)) {
      const [w, h, d] = p.s, [x, y, z] = p.p, e = p.inflate;
      box(x, y, z, x + w, y + h, z + d, p.uv[0], p.uv[1], w, h, d);
      box(x - e, y - e, z - e, x + w + e, y + h + e, z + d + e, p.ov[0], p.ov[1], w, h, d);
    }
    return new Float32Array(v);
  }

  function set(src, slim) {
    if (!gl) return;
    if (!src) { count = 0; draw(); return; }
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, src);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const data = geometry(slim);
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    count = data.length / 6;
    angle = 0.45;   // новый скин — почти анфас
    idleUntil = performance.now() + 1200;
    start();
  }

  function draw() {
    const dpr = window.devicePixelRatio || 1;
    const w = Math.max(1, Math.round((cv.clientWidth || cv.width) * dpr)), h = Math.max(1, Math.round((cv.clientHeight || cv.height) * dpr));
    if (cv.width !== w || cv.height !== h) { cv.width = w; cv.height = h; }
    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    if (!count) return;
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.useProgram(prog);
    const m = M.mul(M.persp(0.62, w / h, 1, 300), M.mul(M.tr(0, 0, -66), M.mul(M.rx(tilt), M.mul(M.ry(angle), M.tr(0, -16.25, 0)))));
    gl.uniformMatrix4fv(gl.getUniformLocation(prog, "m"), false, m);
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    const at = (name, size, off) => { const loc = gl.getAttribLocation(prog, name); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, size, gl.FLOAT, false, 24, off); };
    at("p", 3, 0); at("t", 2, 12); at("l", 1, 20);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.uniform1i(gl.getUniformLocation(prog, "s"), 0);
    gl.drawArrays(gl.TRIANGLES, 0, count);
  }

  function frame(t) {
    raf = 0;
    if (!gl || !count || document.body.dataset.screen !== "skins" || cv.hidden) return;   // экран скрыт — не тратим кадры
    const dt = lastT ? Math.min(64, t - lastT) : 16;
    lastT = t;
    if (dragX === null && t > idleUntil) angle += dt * 0.0006;
    draw();
    raf = requestAnimationFrame(frame);
  }
  function start() { if (!raf && gl) { lastT = 0; raf = requestAnimationFrame(frame); } }
  return { init, set, start, get ok() { return !!gl; } };
})();

// ---------- Скины: библиотека и аккаунт ----------
const SKIN_STUDIO_URL = "https://cisoft.vercel.app/#/games/skin";
const sk = { list: [], sel: null, cache: new Map(), loaded: false, accountFor: null, accountSkin: null, view: "3d" };

function skinSrc(s) {
  if (!sk.cache.has(s.id)) sk.cache.set(s.id, skinCanvas(s.dataUrl));
  return sk.cache.get(s.id);
}

async function skinsLoad() {
  try { sk.list = await call("skinsList"); sk.loaded = true; }
  catch (e) { sk.list = []; toast(e.message, "error"); }
  skinsRender();
}

function skinsRender() {
  const lib = $("#skin-library"), tpl = $("#skin-card-template");
  lib.innerHTML = "";
  for (const s of sk.list) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.id = s.id;
    $(".skin-card-name", n).textContent = s.name;
    $(".skin-card-variant", n).textContent = (s.variant === "slim" ? "Alex · тонкие руки" : "Steve · классика") + (s.height === 32 ? " · 64×32" : "");
    lib.appendChild(n);
    skinSrc(s).then((c) => drawSkin2D($(".skin-thumb", n), c, s.variant === "slim", false)).catch(() => {});
  }
  $("#skin-empty").hidden = sk.list.length > 0;
  if (!sk.list.some((s) => s.id === sk.sel)) sk.sel = sk.list.length ? sk.list[0].id : null;
  skinSelect(sk.sel);
}

function skinSelect(id) {
  sk.sel = id;
  $$(".skin-card").forEach((c) => c.classList.toggle("selected", c.dataset.id === id));
  const s = sk.list.find((x) => x.id === id);
  $("#skin-editor-title").textContent = s ? s.name : "Скин не выбран";
  $("#skin-name").value = s ? s.name : "";
  $("#skin-variant").value = s ? s.variant : "classic";
  for (const b of ["#skin-name", "#skin-variant", "#skin-apply", "#skin-duplicate", "#skin-delete"]) $(b).disabled = !s;
  if (s) skinSrc(s).then((c) => skinPreview(c, s.variant === "slim", s.name + (s.height === 32 ? " · старый формат 64×32" : ""))).catch((e) => toast(e.message, "error"));
  else skinPreview(null, false, "Выберите скин из библиотеки или добавьте PNG");
}

function skinPreview(src, slim, note) {
  $("#skin-stage-note").textContent = note || "";
  drawSkin2D($("#skin-2d"), src, slim, true);
  Skin3D.set(src, slim);
}

function skinView(v) {
  if (v === "3d" && !Skin3D.ok) v = "2d";
  sk.view = v;
  $$(".skin-view").forEach((b) => b.classList.toggle("active", b.dataset.skinView === v));
  $("#skin-3d").hidden = v !== "3d";
  $("#skin-2d").hidden = v !== "2d";
  if (v === "3d") Skin3D.start();
}

function skinImported(r) {
  if (!r) return;
  sk.list = r.skins;
  if (r.added.length) sk.sel = r.added[0];
  skinsRender();
  if (r.added.length) toast(`Добавлено скинов: ${r.added.length}`, "ok");
  if (r.errors.length) toast("Не добавлено: " + r.errors.join("; "), "error");
}

async function skinUpdate(patch) {
  if (!sk.sel) return;
  try { sk.list = await call("skinsUpdate", { skin: sk.sel, ...patch }); skinsRender(); }
  catch (e) { toast(e.message, "error"); }
}

const currentAccount = () => state.accounts.find((a) => a.selected) || state.accounts[0] || null;

async function skinAccountLoad(force) {
  const st = $("#skin-account-status"), acc = currentAccount();
  for (const b of ["#skin-refresh", "#skin-import-account", "#skin-reset", "#cape-hide"]) $(b).disabled = !acc;
  if (!acc) {
    sk.accountFor = null;
    skinAccountRender(null);
    st.textContent = "Войдите в аккаунт Microsoft (вкладка «Аккаунт»), чтобы менять скин и плащ";
    st.dataset.kind = "error";
    return;
  }
  if (sk.accountFor === acc.uuid && !force) return;
  sk.accountFor = acc.uuid;
  st.textContent = "Загружаю профиль Minecraft…";
  st.dataset.kind = "loading";
  try { await skinAccountRender(await call("skinsAccount")); }
  catch (e) { sk.accountFor = null; st.textContent = e.message; st.dataset.kind = "error"; }
}

async function skinAccountRender(p) {
  const cv = $("#skin-current");
  drawSkin2D(cv, null);
  sk.accountSkin = null;
  $("#cape-list").innerHTML = "";
  $("#cape-empty").hidden = true;
  if (!p) return;
  const st = $("#skin-account-status"), a = p.activeSkin;
  st.textContent = `${p.name} · ` + (a ? (a.variant === "slim" ? "тонкие руки (Alex)" : "классическая модель (Steve)") : "стандартный скин");
  st.dataset.kind = "ok";
  if (a && a.dataUrl) {
    try {
      const c = await skinCanvas(a.dataUrl);
      sk.accountSkin = { src: c, slim: a.variant === "slim", name: p.name };
      drawSkin2D(cv, c, a.variant === "slim", false);
    } catch (e) { /* без картинки */ }
  }
  const tpl = $("#cape-template");
  for (const c of p.capes || []) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.cape = c.id;
    n.classList.toggle("active", c.state === "ACTIVE");
    n.title = c.state === "ACTIVE" ? "Этот плащ надет" : "Надеть этот плащ";
    $(".cape-name", n).textContent = c.alias || "Плащ";
    if (c.dataUrl) loadImage(c.dataUrl).then((img) => drawCape($(".cape-thumb", n), img)).catch(() => {});
    $("#cape-list").appendChild(n);
  }
  $("#cape-empty").hidden = (p.capes || []).length > 0;
  $("#cape-hide").disabled = !p.activeCape;
}

function skinsOpen() {
  if (!sk.loaded) skinsLoad();
  skinAccountLoad(false);
  if (sk.view === "3d") Skin3D.start();
}

if (!Skin3D.init($("#skin-3d"))) { $('[data-skin-view="3d"]').hidden = true; skinView("2d"); }
$$(".skin-view").forEach((b) => b.addEventListener("click", () => skinView(b.dataset.skinView)));
$("#skin-library").addEventListener("click", (e) => { const c = e.target.closest(".skin-card"); if (c) skinSelect(c.dataset.id); });
$("#skin-library").addEventListener("keydown", (e) => { const c = e.target.closest(".skin-card"); if (c && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); skinSelect(c.dataset.id); } });
$("#skin-name").addEventListener("change", () => skinUpdate({ name: $("#skin-name").value }));
$("#skin-variant").addEventListener("change", () => skinUpdate({ variant: $("#skin-variant").value }));
$("#skin-add-btn").addEventListener("click", async () => { try { skinImported(await call("skinsPick")); } catch (e) { toast(e.message, "error"); } });
$("#skin-studio-btn").addEventListener("click", () => call("openUrl", { url: SKIN_STUDIO_URL }).catch((e) => toast(e.message, "error")));
$("#skin-duplicate").addEventListener("click", async () => {
  try { sk.list = await call("skinsDuplicate", { skin: sk.sel }); sk.sel = sk.list.length ? sk.list[0].id : null; skinsRender(); toast("Копия добавлена", "ok"); }
  catch (e) { toast(e.message, "error"); }
});
$("#skin-delete").addEventListener("click", async () => {
  const s = sk.list.find((x) => x.id === sk.sel);
  if (!s || !confirm(`Удалить скин «${s.name}» из библиотеки?`)) return;
  try { sk.list = await call("skinsDelete", { skin: s.id }); sk.cache.delete(s.id); skinsRender(); }
  catch (e) { toast(e.message, "error"); }
});
$("#skin-apply").addEventListener("click", () => busy($("#skin-apply"), "Загружаю…", async () => {
  if (!currentAccount()) throw new Error("Сначала войдите в аккаунт Microsoft (вкладка «Аккаунт»)");
  await skinAccountRender(await call("skinsApply", { skin: sk.sel }));
  toast("Скин надет на аккаунт. В игре он обновится после перезахода.", "ok");
}));
$("#skin-refresh").addEventListener("click", () => busy($("#skin-refresh"), "Обновляю…", () => skinAccountLoad(true)));
$("#skin-import-account").addEventListener("click", () => busy($("#skin-import-account"), "Импорт…", async () => {
  sk.list = await call("skinsImportAccount");
  sk.sel = sk.list.length ? sk.list[0].id : null;
  skinsRender();
  toast("Текущий скин аккаунта добавлен в библиотеку", "ok");
}));
$("#skin-reset").addEventListener("click", () => {
  if (!confirm("Сбросить скин аккаунта на стандартный (Steve/Alex)?")) return;
  busy($("#skin-reset"), "Сбрасываю…", async () => { await skinAccountRender(await call("skinsReset")); toast("Скин сброшен", "ok"); });
});
$("#skin-current").addEventListener("click", () => {
  if (sk.accountSkin) skinPreview(sk.accountSkin.src, sk.accountSkin.slim, "Скин аккаунта " + sk.accountSkin.name);
});
$("#cape-list").addEventListener("click", (e) => {
  const b = e.target.closest(".cape-item");
  if (!b || b.classList.contains("active")) return;
  // Без busy(): он заменил бы текст кнопки вместе с картинкой плаща.
  b.disabled = true; b.classList.add("loading");
  call("skinsCape", { cape: b.dataset.cape }).then(skinAccountRender).then(() => toast("Плащ надет", "ok"))
    .catch((err) => { toast(err.message, "error"); b.disabled = false; b.classList.remove("loading"); });
});
$("#cape-hide").addEventListener("click", () => busy($("#cape-hide"), "Снимаю…", async () => {
  await skinAccountRender(await call("skinsCape", { cape: "" }));
  toast("Плащ скрыт", "ok");
}));
// Перетаскивание PNG на экран «Скины» (обработчики для «Модов» — выше, они же отменяют открытие файла).
window.addEventListener("dragover", () => { if (document.body.dataset.screen === "skins") $("#skin-drop").hidden = false; });
window.addEventListener("dragleave", (e) => { if (!e.relatedTarget) $("#skin-drop").hidden = true; });
window.addEventListener("drop", (e) => {
  $("#skin-drop").hidden = true;
  if (document.body.dataset.screen !== "skins" || !e.dataTransfer || !e.dataTransfer.files.length) return;
  callWithFiles("skinsImport", {}, e.dataTransfer.files).then(skinImported).catch((err) => toast(err.message, "error"));
});

// ---------- Серверы ----------
const sv = { list: [], status: {}, pingedAt: 0 };

async function srvOpen() {
  try { sv.list = await call("srvList"); } catch (e) { sv.list = []; toast(e.message, "error"); }
  srvRender();
  if (Date.now() - sv.pingedAt > 60000) srvPingAll();
}

function srvRender() {
  const box = $("#srv-list"), tpl = $("#server-template");
  box.innerHTML = "";
  for (const s of sv.list) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.sid = s.id;
    $(".srv-name", n).textContent = s.name;
    $(".srv-address", n).textContent = s.address;
    srvApply(n, sv.status[s.address]);
    box.appendChild(n);
  }
  $("#srv-empty").hidden = sv.list.length > 0;
}

function renderMotd(el, segs) {
  el.innerHTML = "";
  for (const s of segs || []) {
    const sp = document.createElement("span");
    sp.textContent = s.t;
    if (s.c) sp.style.color = s.c;
    if (s.b) sp.style.fontWeight = "700";
    if (s.i) sp.style.fontStyle = "italic";
    const deco = [s.u && "underline", s.s && "line-through"].filter(Boolean).join(" ");
    if (deco) sp.style.textDecoration = deco;
    el.appendChild(sp);
  }
}

function srvApply(n, r) {
  const motd = $(".srv-motd", n), players = $(".srv-players", n), ver = $(".srv-version", n), ping = $(".srv-ping", n), icon = $(".srv-icon", n);
  players.title = "";
  if (!r) {
    n.dataset.state = "loading";
    motd.textContent = "Опрашиваю сервер…";
    players.textContent = ver.textContent = ping.textContent = "";
    return;
  }
  n.dataset.state = r.online ? "online" : "offline";
  if (r.online) {
    renderMotd(motd, r.motd);
    if (!motd.textContent.trim()) motd.textContent = "Сервер Minecraft";
    players.textContent = `${r.players} / ${r.max} игроков`;
    if (r.sample && r.sample.length) players.title = r.sample.join("\n");
    ver.textContent = r.version || "";
    ping.textContent = `${r.ping} мс`;
    ping.dataset.quality = r.ping < 80 ? "good" : r.ping < 180 ? "ok" : "bad";
    if (r.favicon) icon.src = r.favicon;
  } else {
    motd.textContent = "Недоступен: " + (r.error || "нет ответа");
    players.textContent = ver.textContent = ping.textContent = "";
  }
  n.title = r.resolved ? "Подключение: " + r.resolved + (r.srv ? " (SRV, " + r.srv + ")" : "") : "";
}

async function srvPing(s) {
  delete sv.status[s.address];
  const card = () => $(`.server-card[data-sid="${s.id}"]`);
  if (card()) srvApply(card(), null);
  let r;
  try { r = await call("srvPing", { address: s.address }); } catch (e) { r = { online: false, error: e.message }; }
  sv.status[s.address] = r;
  if (card()) srvApply(card(), r);
}
function srvPingAll() { sv.pingedAt = Date.now(); sv.list.forEach((s) => srvPing(s)); }

async function srvPlay(s) {
  const id = $("#srv-instance").value;
  if (!id) return toast("Сначала создайте инсталляцию", "error");
  $("#instance-select").value = id;
  updatePlayInfo();
  call("selectInstance", { id }).catch(() => {});
  showScreen("play");
  play(s.address);
}

$("#srv-add-form").addEventListener("submit", (e) => {
  e.preventDefault();
  busy($("#srv-add"), "Добавляю…", async () => {
    sv.list = await call("srvAdd", { name: $("#srv-name").value, address: $("#srv-address").value });
    $("#srv-name").value = ""; $("#srv-address").value = "";
    srvRender();
    srvPing(sv.list[sv.list.length - 1]);
  });
});
$("#srv-list").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-action]"), card = e.target.closest(".server-card");
  if (!b || !card) return;
  const s = sv.list.find((x) => x.id === card.dataset.sid);
  if (!s) return;
  switch (b.dataset.action) {
    case "play": srvPlay(s); break;
    case "ping": srvPing(s); break;
    case "copy": call("copyText", { text: s.address }).then(() => toast("Адрес скопирован", "ok")).catch((err) => toast(err.message, "error")); break;
    case "delete":
      if (!confirm(`Убрать «${s.name}» из избранного?`)) return;
      try { sv.list = await call("srvRemove", { sid: s.id }); srvRender(); } catch (err) { toast(err.message, "error"); }
  }
});
$("#srv-refresh").addEventListener("click", srvPingAll);
$("#srv-import").addEventListener("click", () => {
  const id = $("#srv-instance").value;
  if (!id) return toast("Сначала создайте инсталляцию", "error");
  busy($("#srv-import"), "Импорт…", async () => {
    const r = await call("srvImport", { id });
    sv.list = r.servers;
    srvRender();
    srvPingAll();
    toast(`Добавлено серверов: ${r.added}` + (r.skipped ? `, уже были в списке: ${r.skipped}` : ""), "ok");
  });
});

// ---------- Миры, резервные копии, скриншоты ----------
const wr = { tab: "worlds", worlds: [], backups: [], shots: [], seq: 0 };
const wrInst = () => $("#wr-instance").value;
function wrStatus(text, kind = "") { const el = $("#wr-status"); el.textContent = text || ""; el.dataset.kind = kind; }

function wrShow(tab) {
  wr.tab = tab;
  $$(".wr-tab").forEach((b) => b.classList.toggle("active", b.dataset.wrTab === tab));
  for (const t of ["worlds", "backups", "shots"]) $("#wr-panel-" + t).hidden = t !== tab;
  wrLoad();
}

async function wrLoad() {
  const seq = ++wr.seq;
  wrStatus("Загрузка…", "loading");
  try {
    if (wr.tab === "worlds") await wrWorlds();
    else if (wr.tab === "backups") await wrBackups();
    else await wrShots(seq);
    if (seq === wr.seq) wrStatus("", "");
  } catch (e) { if (seq === wr.seq) wrStatus(e.message, "error"); }
}

async function wrWorlds() {
  wr.worlds = await call("worldsList", { id: wrInst() });
  const box = $("#world-list"), tpl = $("#world-template");
  box.innerHTML = "";
  for (const w of wr.worlds) {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.instance = w.instance;
    n.dataset.folder = w.folder;
    n.classList.toggle("running", !!w.running);
    if (w.icon) $(".world-icon", n).src = w.icon;
    $(".world-name", n).textContent = w.name;
    $(".world-instance", n).textContent = w.instanceName;
    $(".world-version", n).textContent = w.version || "";
    $(".world-version", n).hidden = !w.version;
    $(".world-mode", n).textContent = w.mode || "";
    $(".world-size", n).textContent = fmtSize(w.size);
    $(".world-played", n).textContent = "Играли " + fmtDate(w.lastPlayed);
    $(".world-folder", n).textContent = w.folder !== w.name ? "Папка: " + w.folder : "";
    box.appendChild(n);
  }
  $("#worlds-empty").hidden = wr.worlds.length > 0;
}

async function wrBackups() {
  wr.backups = await call("backupsList", { id: wrInst() });
  const box = $("#backup-list"), tpl = $("#backup-template");
  box.innerHTML = "";
  wr.backups.forEach((b, idx) => {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.idx = idx;
    n.classList.toggle("orphan", !b.instanceExists);
    $(".backup-name", n).textContent = b.world;
    $(".backup-name", n).title = b.file;
    $(".backup-instance", n).textContent = b.instanceName;
    $(".backup-meta", n).textContent = `${fmtDate(b.created)} · ${fmtSize(b.size)}`;
    box.appendChild(n);
  });
  $("#backups-empty").hidden = wr.backups.length > 0;
}

async function wrShots(seq) {
  wr.shots = await call("shotsList", { id: wrInst(), limit: 300 });
  const grid = $("#shot-grid"), tpl = $("#shot-template"), cards = new Map();
  grid.innerHTML = "";
  wr.shots.forEach((s, idx) => {
    const n = tpl.content.firstElementChild.cloneNode(true);
    n.dataset.idx = idx;
    n.classList.toggle("no-thumb", !s.thumb);
    if (s.thumb) $(".shot-img", n).src = s.thumb;
    $(".shot-name", n).textContent = s.file;
    $(".shot-meta", n).textContent = `${s.instanceName} · ${fmtDate(s.date)} · ${fmtSize(s.size)}`;
    cards.set(s, n);
    grid.appendChild(n);
  });
  $("#shots-empty").hidden = wr.shots.length > 0;
  // Миниатюры, которых ещё нет в кэше, — пачками по 12.
  const missing = wr.shots.filter((s) => !s.thumb);
  for (let i = 0; i < missing.length; i += 12) {
    if (seq !== wr.seq || wr.tab !== "shots" || document.body.dataset.screen !== "worlds") return;
    const part = missing.slice(i, i + 12);
    const r = await call("shotsThumbs", { items: part.map((s) => ({ instance: s.instance, file: s.file })) });
    for (const s of part) {
      const url = r[s.instance + "/" + s.file], n = cards.get(s);
      if (url && n) { s.thumb = url; $(".shot-img", n).src = url; n.classList.remove("no-thumb"); }
    }
  }
}

$$(".wr-tab").forEach((b) => b.addEventListener("click", () => wrShow(b.dataset.wrTab)));
$("#wr-instance").addEventListener("change", () => wrLoad());
$("#world-list").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-action]"), card = e.target.closest(".world-card");
  if (!b || !card) return;
  const w = wr.worlds.find((x) => x.instance === card.dataset.instance && x.folder === card.dataset.folder);
  if (!w) return;
  const args = { id: w.instance, folder: w.folder };
  switch (b.dataset.action) {
    case "backup":
      busy(b, "Копирую…", async () => {
        const r = await call("worldBackup", args);
        toast(`Копия «${w.name}» готова: ${fmtSize(r.zipSize)}, файлов ${r.files}` + (r.warning ? ". " + r.warning : ""), r.warning ? "error" : "ok");
      });
      break;
    case "folder": call("worldsOpen", { what: "world", ...args }).catch((err) => toast(err.message, "error")); break;
    case "delete":
      if (!confirm(`Удалить мир «${w.name}» (${w.instanceName})? Папка уйдёт в Корзину Windows.`)) return;
      try { await call("worldDelete", args); toast("Мир перемещён в Корзину", "ok"); wrLoad(); } catch (err) { toast(err.message, "error"); }
  }
});
$("#backup-list").addEventListener("click", async (e) => {
  const btn = e.target.closest("[data-action]"), row = e.target.closest(".backup-row");
  if (!btn || !row) return;
  const b = wr.backups[+row.dataset.idx];
  if (!b) return;
  if (btn.dataset.action === "restore") {
    const target = b.instanceExists ? b.instance : wrInst();
    if (!target) return toast("Инсталляция этой копии удалена — выберите инсталляцию вверху, и копия восстановится в неё", "error");
    const tname = (state.instances.find((i) => i.id === target) || {}).name || target;
    if (!confirm(`Восстановить «${b.world}» новой копией мира в «${tname}»? Существующие миры не изменятся.`)) return;
    busy(btn, "Восстанавливаю…", async () => {
      const r = await call("backupRestore", { id: b.instance, file: b.file, target });
      toast(`Мир восстановлен в папку «${r.folder}»`, "ok");
    });
  } else if (btn.dataset.action === "delete") {
    if (!confirm(`Удалить копию ${b.file}? Файл уйдёт в Корзину Windows.`)) return;
    try { await call("backupDelete", { id: b.instance, file: b.file }); wrLoad(); } catch (err) { toast(err.message, "error"); }
  }
});
$("#backups-folder").addEventListener("click", () => call("worldsOpen", { what: "backups", id: wrInst() }).catch((err) => toast(err.message, "error")));
$("#shot-grid").addEventListener("click", async (e) => {
  const b = e.target.closest("[data-action]"), card = e.target.closest(".shot-card");
  if (!b || !card) return;
  const s = wr.shots[+card.dataset.idx];
  if (!s) return;
  const args = { id: s.instance, file: s.file };
  switch (b.dataset.action) {
    case "open": call("shotOpen", args).catch((err) => toast(err.message, "error")); break;
    case "copy": call("shotCopy", args).then(() => toast("Скриншот скопирован в буфер обмена", "ok")).catch((err) => toast(err.message, "error")); break;
    case "delete":
      if (!confirm(`Удалить ${s.file}? Файл уйдёт в Корзину Windows.`)) return;
      try { await call("shotDelete", args); card.remove(); toast("Скриншот перемещён в Корзину", "ok"); } catch (err) { toast(err.message, "error"); }
  }
});

// ---------- Автонастройка графики (окно из карточки инсталляции) ----------
let gfxInst = null;
const GFX_KEYS = {
  renderDistance: "Дальность прорисовки", simulationDistance: "Дальность симуляции", graphicsMode: "Графика", maxFps: "Макс. FPS",
  enableVsync: "Вертикальная синхронизация", particles: "Частицы", entityDistanceScaling: "Дальность отрисовки существ",
  biomeBlendRadius: "Смешивание биомов", mipmapLevels: "Уровни MIP-текстур", ao: "Мягкое освещение", entityShadows: "Тени существ",
  renderClouds: "Облака", prioritizeChunkUpdates: "Приоритет обновления чанков", fullscreen: "Полный экран",
  "quality.weather_quality": "Sodium: погода", "quality.leaves_quality": "Sodium: листва", "quality.enable_vignette": "Sodium: виньетка",
  "performance.chunk_builder_threads": "Sodium: потоки сборки чанков", "performance.always_defer_chunk_updates_v2": "Sodium: отложенные обновления чанков",
  "performance.animate_only_visible_textures": "Sodium: анимация только видимых текстур", "performance.use_entity_culling": "Sodium: отсечение существ",
  "performance.use_fog_occlusion": "Sodium: отсечение туманом", "performance.use_block_face_culling": "Sodium: отсечение граней блоков",
  "performance.use_no_error_g_l_context": "Sodium: контекст OpenGL без проверок", "advanced.cpu_render_ahead_limit": "Sodium: кадров вперёд",
};
const GFX_ENUMS = { graphicsMode: ["Быстрая", "Детальная", "Улучшенная"], particles: ["Все", "Меньше", "Минимум"], prioritizeChunkUpdates: ["Нет", "От игрока", "Рядом"] };
function gfxValue(key, v) {
  if (v === null || v === undefined) return "—";
  v = String(v).replace(/^"|"$/g, "");
  const en = GFX_ENUMS[key];
  if (en && en[+v] !== undefined) return en[+v];
  if (key === "maxFps" && v === "260") return "без ограничения";
  if (key === "performance.chunk_builder_threads" && v === "0") return "авто";
  return { true: "вкл", false: "выкл", fast: "быстрые", FAST: "быстро", DEFAULT: "по умолчанию", FANCY: "красиво" }[v] || v;
}
function gfxStatus(text, kind = "") { const el = $("#gfx-status"); el.textContent = text || ""; el.dataset.kind = kind; }
function gfxMark(profile) { $$(".gfx-profile").forEach((b) => b.classList.toggle("active", b.dataset.profile === profile)); }
function gfxHardware(h, sodium) {
  const box = $("#gfx-hw");
  box.innerHTML = "";
  const gb = (mb) => (mb / 1024).toFixed(mb >= 10240 ? 0 : 1) + " ГБ";
  const items = [
    ["Видеокарта", (h.gpu || "не определена") + (h.vramMb ? ` · ${gb(h.vramMb)}` : "") + (h.gpu && !h.discrete ? " · встроенная" : "")],
    ["Процессор", `${h.cpu || "?"} · ${h.cores} ядер / ${h.threads} потоков`],
    ["Память", gb(h.ramMb)],
    ["Экран", `${h.screen ? h.screen + " · " : ""}${h.refreshHz} Гц`],
    ["Уровень ПК", h.tierName + (sodium ? " · Sodium установлен" : "")],
  ];
  for (const [k, v] of items) {
    const d = document.createElement("div");
    d.className = "gfx-hw-item";
    const a = document.createElement("span"); a.className = "muted"; a.textContent = k;
    const b = document.createElement("strong"); b.textContent = v;
    d.append(a, b);
    box.appendChild(d);
  }
}
function gfxChanges(list) {
  const ul = $("#gfx-changes");
  ul.innerHTML = "";
  for (const c of list) {
    const li = document.createElement("li");
    li.dataset.file = c.file;
    li.textContent = `${GFX_KEYS[c.key] || c.key}: ${gfxValue(c.key, c.from)} → ${gfxValue(c.key, c.to)}`;
    ul.appendChild(li);
  }
}
async function openGfx(inst) {
  gfxInst = inst;
  $("#gfx-title").textContent = `Автонастройка графики — ${inst.name}`;
  $("#gfx-changes").innerHTML = "";
  $("#gfx-hw").textContent = "Определяю видеокарту, процессор и экран…";
  $("#gfx-restore").hidden = true;
  gfxStatus("", "");
  gfxMark("");
  openModal("#gfx-modal");
  try {
    const st = await call("gfxStatus", { id: inst.id });
    gfxHardware(st.hardware, st.sodium);
    $("#gfx-restore").hidden = !st.hasBackup;
    if (st.profile) { gfxMark(st.profile); gfxStatus(`Сейчас применён профиль (с ${st.backupDate}). Можно выбрать другой или вернуть как было.`, ""); }
  } catch (e) { $("#gfx-hw").textContent = "Железо не определено"; gfxStatus(e.message, "error"); }
}
$$(".gfx-profile").forEach((b) => b.addEventListener("click", async () => {
  if (!gfxInst) return;
  $$(".gfx-profile").forEach((x) => (x.disabled = true));
  b.classList.add("loading");
  gfxStatus("Записываю настройки…", "loading");
  try {
    const r = await call("gfxApply", { id: gfxInst.id, profile: b.dataset.profile });
    gfxMark(r.profile);
    gfxChanges(r.changes);
    $("#gfx-restore").hidden = !r.hasBackup;
    gfxStatus(r.changes.length ? `Готово: изменено параметров — ${r.changes.length}. Запустите игру, чтобы увидеть результат.` : "Настройки уже такие — ничего менять не пришлось.", "ok");
  } catch (e) { gfxStatus(e.message, "error"); }
  finally { $$(".gfx-profile").forEach((x) => (x.disabled = false)); b.classList.remove("loading"); }
}));
$("#gfx-restore").addEventListener("click", () => {
  if (!gfxInst || !confirm("Вернуть options.txt (и настройки Sodium) к состоянию до автонастройки?")) return;
  busy($("#gfx-restore"), "Возвращаю…", async () => {
    const r = await call("gfxRestore", { id: gfxInst.id });
    gfxMark("");
    $("#gfx-changes").innerHTML = "";
    gfxStatus("Возвращено: " + (r.restored.length ? r.restored.join(", ") : "нечего было возвращать"), "ok");
    $("#gfx-restore").hidden = true;
  });
});

// ---------- Игровой режим: подсказка в настройках ----------
let gameModeHwShown = false;
async function gameModeHint() {
  if (gameModeHwShown || !host) return;
  try {
    const h = await call("hardware");
    gameModeHwShown = true;
    let text = `На этом ПК: ${h.gpu || "видеокарта не определена"}` + (h.discrete ? "" : " (встроенная — переключать некуда)") + ` · экран ${h.refreshHz} Гц`;
    try {
      const gm = await call("gameModeInfo");
      const p = gm.power || {};
      if (!p.error) text += !p.target ? ". Схемы «Высокая производительность» в Windows нет — питание во время игры не меняется."
        : p.target === p.active ? ". Схема «Высокая производительность» уже включена." : "";
    } catch (e) { /* без сведений о питании */ }
    $("#gamemode-hw").textContent = text;
  } catch (e) { /* без подсказки */ }
}

// ---------- старт ----------
// Первый запрос, отправленный пока страница ещё грузится, программа может не получить —
// повторяем, пока не придёт ответ.
function withTimeout(p, ms) {
  return Promise.race([p, new Promise((_, rej) => setTimeout(() => rej(new Error("timeout")), ms))]);
}
async function boot() {
  for (let i = 0; ; i++) {
    try { await withTimeout(refresh(), 2000); return; }
    catch (e) { if (e.message !== "timeout" || i >= 15) throw e; }
  }
}
boot().then(() => {
  if (!state.instances.length) showScreen("instances");
  else if (!state.accounts.length) showScreen("account");
}).catch((e) => status("Ошибка: " + e.message, "error"));
