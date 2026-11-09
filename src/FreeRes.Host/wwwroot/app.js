"use strict";

// Interface Facil FreePC. Tout texte venant des donnees passe par textContent (jamais innerHTML) :
// aucune injection possible depuis un nom de programme, de modele ou de navigateur.
// L'interface ne fait que DECIDER des propositions du moteur ; elle ne peut demander aucune autre action.

const REFRESH_MS = 10000;
const MODULE_REFRESH_MS = 30000;
const nf1 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 1 });
const nf2 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 2 });
const SEVERITY = { Crit: "Critique", Warn: "Alerte", Info: "Conseil" };
const STARTUP_KIND = { Run: "Registre", StartupFolder: "Démarrage", Service: "Service", ScheduledTask: "Tâche planifiée" };
const VIEWS = ["dashboard", "apps", "browsers", "ai", "docker", "history", "help"];

let state = null;
let currentView = "dashboard";
let moduleTimer = null;

// ------------------------------------------------------------------ outils
function el(id) { return document.getElementById(id); }

function make(tag, props, ...children) {
  const node = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === undefined || v === null) { continue; }
      if (k === "text") { node.textContent = v; }
      else if (k === "class") { node.className = v; }
      else if (k.startsWith("data-")) { node.setAttribute(k, v); }
      else { node[k] = v; }
    }
  }
  for (const c of children) {
    if (c === null || c === undefined || c === false) { continue; }
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return node;
}

const SVGNS = "http://www.w3.org/2000/svg";
const SEVERITY_ICON = { Crit: "triangle-alert", Warn: "circle-alert", Info: "info" };

// Icone monochrome du sprite (decorative : le texte voisin porte le sens).
function icon(name, cls) {
  const svg = document.createElementNS(SVGNS, "svg");
  svg.setAttribute("class", "i" + (cls ? " " + cls : ""));
  svg.setAttribute("aria-hidden", "true");
  const use = document.createElementNS(SVGNS, "use");
  use.setAttribute("href", "icons.svg#" + name);
  svg.append(use);
  return svg;
}

// Icone reelle de l'element, extraite de son executable par FreePC (200 = PNG, 204 = pas d'icone) ; l'icone
// monochrome reste affichee sinon. Une requete par element et par session de la page (presences et absences
// retenues) ; l'image passe en data: (seule source d'image autorisee avec 'self' par la politique de securite).
const iconCache = new Map();
function iconData(src) {
  if (!iconCache.has(src)) {
    iconCache.set(src, fetch(src, { credentials: "same-origin" })
      .then(r => r.status === 200 ? r.blob() : null)
      .then(blob => blob && new Promise(resolve => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => resolve(null);
        reader.readAsDataURL(blob);
      }))
      .catch(() => null));
  }
  return iconCache.get(src);
}

function realIcon(module, id, fallback, large) {
  const box = make("span", { class: "ico" + (large ? " lg" : "") }, icon(fallback));
  if (id) {
    iconData("/api/icons/" + module + "/" + encodeURIComponent(id)).then(url => {
      if (url) { box.replaceChildren(make("img", { alt: "", src: url })); }
    });
  }
  return box;
}

// Libelle d'un bouton a icone (le texte reste dans un span : on peut le changer sans perdre l'icone).
function iconButton(name, label, props) {
  return make("button", Object.assign({ type: "button" }, props), icon(name), make("span", { text: label }));
}

function gb(v) { return v === null || v === undefined ? "—" : nf2.format(v) + " GB"; }
function bytesGB(b) { return gb(b / 1073741824); }
function time(iso) { return new Date(iso).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" }); }
function dayTime(iso) {
  const d = new Date(iso);
  const today = new Date().toDateString() === d.toDateString();
  return today ? time(iso) : d.toLocaleDateString("fr-FR", { day: "2-digit", month: "2-digit" }) + " " + time(iso);
}
function plural(n, one, many) { return n + " " + (n > 1 ? many : one); }
function hours(h) { return h >= 48 ? nf1.format(h / 24) + " jours" : nf1.format(h) + " h"; }

async function api(path, options) {
  const res = await fetch(path, Object.assign({ credentials: "same-origin", cache: "no-store" }, options));
  if (!res.ok && res.status !== 404 && res.status !== 422) { throw new Error("HTTP " + res.status); }
  const type = res.headers.get("content-type") || "";
  const body = res.status === 204 ? null : type.includes("json") ? await res.json() : await res.text();
  return { status: res.status, body };
}

function post(path) { return api(path, { method: "POST" }); }

// ------------------------------------------------------------------ navigation
function showView(name) {
  if (!VIEWS.includes(name)) { name = "dashboard"; }
  currentView = name;
  for (const v of VIEWS) {
    el("view-" + v).hidden = v !== name;
    el("tab-" + v).setAttribute("aria-selected", String(v === name));
  }
  if (location.hash !== "#" + name) { history.replaceState(null, "", "#" + name); }
  clearInterval(moduleTimer);
  moduleTimer = null;
  if (name === "help") { startTour(); } else { tour.root.classList.add("paused"); }
  const loaders = { apps: loadApps, browsers: loadBrowsers, ai: loadAi, docker: loadDocker, history: loadHistory };
  if (loaders[name]) {
    loaders[name]();
    moduleTimer = setInterval(loaders[name], MODULE_REFRESH_MS);
  }
}

document.querySelector(".tabs").addEventListener("click", e => {
  const b = e.target.closest("button[data-view]");
  if (b) { showView(b.dataset.view); }
});

// Lien direct ou retour arriere vers une vue (#apps, #help...).
window.addEventListener("hashchange", () => {
  const name = location.hash.slice(1);
  if (name !== currentView) { showView(name); }
});

document.querySelector(".tabs").addEventListener("keydown", e => {
  if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") { return; }
  const i = VIEWS.indexOf(currentView) + (e.key === "ArrowRight" ? 1 : -1);
  const next = VIEWS[(i + VIEWS.length) % VIEWS.length];
  showView(next);
  el("tab-" + next).focus();
});

// ------------------------------------------------------------------ tableau de bord
// level : "ok" | "warn" | "crit" ; les reperes sont places en % de la piste.
function setMeter(id, value, fillPct, level, sub, warnPct, critPct) {
  const m = el(id);
  m.querySelector("[data-v]").textContent = value;
  m.querySelector("[data-b]").style.width = clamp(fillPct) + "%";
  m.querySelector("[data-w]").style.left = clamp(warnPct) + "%";
  m.querySelector("[data-c]").style.left = clamp(critPct) + "%";
  m.dataset.level = level;
  m.querySelector("[data-s]").textContent = sub;
}
function clamp(p) { return Math.max(0, Math.min(100, p)).toFixed(1); }
function levelLow(v, warn, crit) { return v < crit ? "crit" : v < warn ? "warn" : "ok"; }
function levelHigh(v, warn, crit) { return v >= crit ? "crit" : v >= warn ? "warn" : "ok"; }

function renderState(s) {
  state = s;
  const t = s.thresholds, m = s.snapshot, p = s.profile;

  setMeter("m-ram", gb(m.ramAvailGB), 100 * m.ramAvailGB / m.ramTotalGB, levelLow(m.ramAvailGB, t.ramAvailWarnGB, t.ramAvailCritGB),
    "sur " + gb(m.ramTotalGB) + ", alerte sous " + gb(t.ramAvailWarnGB),
    100 * t.ramAvailWarnGB / m.ramTotalGB, 100 * t.ramAvailCritGB / m.ramTotalGB);
  setMeter("m-commit", nf1.format(m.commitPct) + " %", m.commitPct, levelHigh(m.commitPct, t.commitWarnPct, t.commitCritPct),
    gb(m.commitUsedGB) + " sur " + gb(m.commitLimitGB) + " (mémoire vive et fichier d'échange)",
    t.commitWarnPct, t.commitCritPct);
  setMeter("m-disk", gb(m.systemDriveFreeGB), 100 * m.systemDriveFreeGB / m.systemDriveTotalGB,
    levelLow(m.systemDriveFreeGB, t.diskFreeWarnGB, t.diskFreeCritGB),
    m.systemDrive + " sur " + gb(m.systemDriveTotalGB) + ", alerte sous " + gb(t.diskFreeWarnGB),
    100 * t.diskFreeWarnGB / m.systemDriveTotalGB, 100 * t.diskFreeCritGB / m.systemDriveTotalGB);

  el("top").replaceChildren(...m.topProcesses.slice(0, 8).map(pr =>
    make("li", null, make("span", { text: pr.name + (pr.count > 1 ? " (" + pr.count + ")" : "") }), make("span", { class: "num", text: gb(pr.workingSetGB) }))));

  renderPending(s);

  el("auto").replaceChildren(...s.recentAuto.map(logItem));
  el("auto-empty").hidden = s.recentAuto.length > 0;

  const health = el("health");
  const anyWarn = ["m-ram", "m-commit", "m-disk"].some(id => el(id).dataset.level === "warn");
  health.dataset.level = s.critical ? "crit" : anyWarn ? "warn" : "ok";
  health.textContent = s.critical ? "État critique" : anyWarn ? "À surveiller" : "Tout va bien";
  el("busy").hidden = !s.decisionInProgress;

  el("machine").textContent = [p.manufacturer, p.model].filter(Boolean).join(" ") + ", " + p.osName +
    (p.osDisplayVersion ? " " + p.osDisplayVersion : "") + ", " + nf1.format(p.ramTotalGB) + " GB de mémoire";
  el("cycle").textContent = s.lastCycle
    ? "Dernière analyse à " + time(s.lastCycle.at) + ", puis toutes les " + s.monitorIntervalMinutes + " min" +
      (s.lastCycle.errors.length ? " (" + s.lastCycle.errors.length + " module(s) en erreur)" : "")
    : "Première analyse en cours";
  el("data-dir").textContent = s.dataDirectory || "";
  document.querySelector("[data-snooze]").textContent = hours(s.snoozeHours);
  document.querySelector("[data-reject]").textContent = hours(s.rejectHours);
}

function renderPending(s) {
  const busyIds = new Set([...document.querySelectorAll(".decision[data-busy]")].map(n => n.dataset.id));
  el("pending").replaceChildren(...s.pending.map(d => {
    const actions = make("div", { class: "actions" },
      iconButton("check", "Appliquer", { class: "btn primary", "data-act": "approve" }),
      iconButton("clock", "Plus tard", { class: "btn", "data-act": "snooze", title: "Revient dans " + hours(s.snoozeHours) }),
      iconButton("x", "Ignorer", { class: "btn", "data-act": "reject", title: "Ignorée pendant " + hours(s.rejectHours) }));
    const tags = make("div", { class: "tags" },
      make("span", { class: "tag sev-" + d.severity }, icon(SEVERITY_ICON[d.severity] || "info"), SEVERITY[d.severity] || d.severity),
      d.estGainGB > 0 ? make("span", { class: "tag", text: "gain estimé " + gb(d.estGainGB) }) : null,
      d.requiresAdmin ? make("span", { class: "tag" }, icon("shield"), "Windows demandera votre accord administrateur") : null);
    const li = make("li", { class: "decision", "data-severity": d.severity, "data-id": d.id },
      make("p", { class: "title", text: d.title }), actions, make("p", { class: "detail", text: d.detail }), tags);
    if (busyIds.has(d.id)) { setBusy(li, true); }
    return li;
  }));
  el("pending-count").textContent = s.pending.length;
  el("pending-empty").hidden = s.pending.length > 0;
  el("pending").hidden = s.pending.length === 0;
}

function setBusy(li, busy) {
  if (busy) { li.dataset.busy = "1"; } else { delete li.dataset.busy; }
  li.querySelectorAll("button").forEach(b => { b.disabled = busy; });
}

function feedback(ok, message) {
  const f = el("feedback");
  f.hidden = false;
  f.dataset.ok = String(ok);
  f.textContent = message;
}

el("pending").addEventListener("click", async e => {
  const b = e.target.closest("button[data-act]");
  if (!b) { return; }
  const li = b.closest(".decision");
  const id = li.dataset.id;
  const title = li.querySelector(".title").textContent;
  const act = b.dataset.act;
  setBusy(li, true);
  if (act === "approve") { el("busy").hidden = false; }
  try {
    const r = await post("/api/proposals/" + encodeURIComponent(id) + "/" + act);
    if (r.status === 404) {
      feedback(true, "« " + title + " » a déjà été traitée ou n'est plus utile.");
    } else if (r.status === 422) {
      feedback(false, "« " + title + " » refusée : " + r.body);
    } else if (act === "approve") {
      const res = r.body;
      const gain = res.diskDeltaGB > 0 ? " (" + gb(res.diskDeltaGB) + " libérés sur le disque)" : "";
      feedback(res.ok, (res.ok ? "Appliqué : " : "Échec : ") + res.message + gain +
        (res.stillPending ? ". La décision reste en attente." : ""));
    } else {
      feedback(true, act === "snooze"
        ? "« " + title + " » reviendra dans " + hours(state.snoozeHours) + "."
        : "« " + title + " » est ignorée pendant " + hours(state.rejectHours) + ".");
    }
  } catch (err) {
    feedback(false, "La décision n'a pas pu être envoyée (" + err.message + "). Réessayez.");
  } finally {
    setBusy(li, false);
    await refresh();
  }
});

el("analyze").addEventListener("click", async () => {
  const b = el("analyze");
  const label = b.querySelector("span");
  b.disabled = true;
  b.querySelector("svg").classList.add("spin");
  label.textContent = "Analyse…";
  try {
    const r = await post("/api/cycle");
    const c = r.body;
    feedback(c.errors.length === 0, c.newProposals > 0
      ? "Analyse terminée : " + c.newProposals + " nouvelle(s) décision(s)."
      : "Analyse terminée : rien de nouveau." + (c.errors.length ? " Modules en erreur : " + c.errors.join(" ; ") : ""));
  } catch (err) {
    feedback(false, "L'analyse a échoué (" + err.message + ").");
  } finally {
    b.disabled = false;
    b.querySelector("svg").classList.remove("spin");
    label.textContent = "Analyser";
    await refresh();
  }
});

function logItem(e) {
  return make("li", { "data-ok": String(e.ok) },
    make("span", { class: "when", text: dayTime(e.at) }),
    make("span", { class: "what", text: e.message }),
    make("span", { class: "gain", text: e.diskDeltaGB > 0 ? "+" + gb(e.diskDeltaGB) : "" }));
}

// ------------------------------------------------------------------ modules
async function module(name) {
  const r = await api("/api/modules/" + name);
  return r.status === 200 ? r.body.status : null;
}

function waiting(target, text) {
  target.replaceChildren(make("p", { class: "muted empty-panel", text: text }));
}

function card(title, big, rows, note, iconEl) {
  const dl = make("dl");
  for (const [k, v] of rows) {
    if (v === null || v === undefined || v === "") { continue; }
    dl.append(make("dt", { text: k }), make("dd", { text: v }));
  }
  return make("li", { class: "card" },
    make("div", { class: "head" }, make("h3", null, iconEl || null, title), big ? make("span", { class: "big", text: big }) : null),
    dl, note ? make("p", { class: "small muted", text: note }) : null);
}

async function loadApps() {
  try {
    const inv = await module("apps");
    if (!inv) { waiting(el("apps-usage"), "Inventaire au prochain cycle d'analyse."); el("apps-startup").replaceChildren(); return; }
    const usage = [...inv.usage].filter(u => u.processCount > 0).sort((a, b) => b.workingSetBytes - a.workingSetBytes);
    el("apps-usage").replaceChildren(...usage.map(u => make("tr", null,
      make("td", { title: u.name }, make("div", { class: "cell" }, realIcon("apps", u.appId, "app-window"), make("span", { class: "label", text: u.name }))),
      make("td", { class: "num", text: u.processCount }),
      make("td", { class: "num", text: gb(u.workingSetGB) }),
      make("td", null, make("span", { class: u.background ? "pill bg" : "pill", text: u.background ? "Arrière-plan" : "Avec fenêtre" })))));
    el("apps-meta").textContent = plural(inv.apps.length, "application installée", "applications installées") + ", " + plural(usage.length, "active", "actives");
    const startup = [...inv.startupItems].sort((a, b) => Number(b.enabled) - Number(a.enabled) || a.name.localeCompare(b.name, "fr"));
    el("apps-startup").replaceChildren(...startup.map(s => make("tr", null,
      make("td", { title: s.command || s.name }, make("div", { class: "cell" }, realIcon("apps", s.id, "power"), make("span", { class: "label", text: s.name }))),
      make("td", { text: STARTUP_KIND[s.kind] || s.kind }),
      make("td", null, make("span", { class: s.enabled && !s.protection ? "pill on" : "pill", text: s.protection ? "Protégé" : s.enabled ? "Activé" : "Désactivé", title: s.protection || "" })))));
    el("startup-meta").textContent = plural(startup.filter(s => s.enabled).length, "activé", "activés") + " sur " + startup.length;
  } catch (err) { waiting(el("apps-usage"), "Lecture impossible (" + err.message + ")."); }
}

async function loadBrowsers() {
  const list = el("browsers");
  try {
    const browsers = await module("browsers");
    if (!browsers || browsers.length === 0) { waiting(list, browsers ? "Aucun navigateur reconnu sur ce PC." : "État au prochain cycle d'analyse."); return; }
    list.replaceChildren(...browsers.map(b => card(b.name, b.running ? gb(b.workingSetGB) : "Fermé", [
      ["Processus", b.running ? String(b.processCount) : null],
      ["Ouvert depuis", b.uptimeHours !== null && b.uptimeHours !== undefined ? hours(b.uptimeHours) : null],
      ["Mise en veille des onglets", b.memorySaverNote],
      ["Profils ouverts", b.activeProfiles > 0 ? String(b.activeProfiles) : null],
      ["Extension FreePC", b.extensionInstalled === null || b.extensionInstalled === undefined ? null : b.extensionInstalled ? "Installée" : "Absente"],
    ], "Redémarrage : " + b.restartNote, realIcon("browsers", b.id, "globe", true))));
  } catch (err) { waiting(list, "Lecture impossible (" + err.message + ")."); }
}

async function loadAi() {
  const list = el("ai");
  try {
    const apps = await module("ai");
    if (!apps || apps.length === 0) {
      waiting(list, apps ? "Aucune application d'IA locale détectée (Ollama, LM Studio, GPT4All, Jan)." : "État au prochain cycle d'analyse.");
      return;
    }
    list.replaceChildren(...apps.map(a => card(a.name, a.processCount > 0 ? gb(a.workingSetGB) : null, [
      ["Installée", a.installed ? "Oui" : "Non (serveur seul)"],
      ["Modèles sur le disque", a.modelsDir ? gb(a.modelsDiskGB) : null],
      ["Dossier des modèles", a.modelsDir],
      ["Modèles installés", a.installedModelCount > 0 ? String(a.installedModelCount) : null],
      ["Modèles chargés", a.loadedModels.length ? a.loadedModels.map(m => m.name + " " + bytesGB(m.sizeBytes) + " (graphique " + bytesGB(m.vramBytes) + ")").join(", ") : null],
    ], a.note, realIcon("ai", a.id, "brain-circuit", true))));
  } catch (err) { waiting(list, "Lecture impossible (" + err.message + ")."); }
}

async function loadDocker() {
  const box = el("docker");
  try {
    const d = await module("docker");
    if (!d || !d.installed) { waiting(box, d ? "Docker Desktop n'est pas installé sur ce PC." : "État au prochain cycle d'analyse."); return; }
    const u = d.usage;
    const rows = [
      ["État", d.running ? "Démarré" : "Arrêté"],
      ["Disque virtuel (VHDX)", u ? gb(u.vhdxGB) : null],
      ["Données dans Docker", u ? gb(u.insideUsedGB) : null],
      ["Images", u && u.imagesGB !== null ? gb(u.imagesGB) + " dont " + gb(u.imagesReclaimableGB) + " récupérables" : null],
      ["Volumes (jamais purgés)", u && u.volumesGB !== null ? gb(u.volumesGB) : null],
      ["Cache de construction", u && u.buildCacheGB !== null ? gb(u.buildCacheGB) + " dont " + gb(u.buildCacheReclaimableGB) + " récupérables" : null],
      ["Mesuré", u ? dayTime(u.measuredAt) : "Pas encore (Docker doit être démarré)"],
      ["Dernier nettoyage interne (fstrim)", d.trimmedAt ? dayTime(d.trimmedAt) : "Jamais"],
      ["Dernier compactage", d.compactedAt ? dayTime(d.compactedAt) : "Jamais"],
    ];
    box.replaceChildren(make("ul", { class: "cards" }, card("Docker Desktop", u ? gb(u.vhdxGB) : null, rows,
      "Le disque virtuel de Docker ne rétrécit jamais seul : FreePC récupère l'espace libéré à l'intérieur, puis propose le compactage quand le gain réel le justifie.",
      realIcon("docker", "desktop", "container", true))));
  } catch (err) { waiting(box, "Lecture impossible (" + err.message + ")."); }
}

// ------------------------------------------------------------------ visite « Comment ça marche »
// Le minutage vient de la barre de progression du chapitre actif (animation CSS « progress ») : sa fin passe au
// chapitre suivant ; la pause gele toutes les animations de la visite. Une seule lecture, puis « Revoir ».
// Si Windows demande de réduire les animations : aucune lecture automatique, chaque chapitre s'affiche figé.
const tour = {
  root: document.querySelector(".tour"),
  stage: el("stage"),
  chapters: el("chapters"),
  play: el("tour-play"),
  reduced: window.matchMedia("(prefers-reduced-motion: reduce)"),
  scene: 1,
  ended: false,
};

function tourScene(n) {
  tour.scene = n;
  const items = [...tour.chapters.children];
  // Retirer puis remettre l'etat (apres un recalcul) relance les animations, meme pour la scene en cours.
  items.forEach(li => li.querySelector("button").removeAttribute("aria-current"));
  tour.stage.dataset.scene = "0";
  void tour.root.offsetWidth;
  items.forEach((li, i) => {
    const current = i + 1 === n;
    if (current) { li.querySelector("button").setAttribute("aria-current", "step"); }
    li.querySelector("p").hidden = !current;
    if (i + 1 < n) { li.dataset.done = "1"; } else { delete li.dataset.done; }
  });
  tour.stage.dataset.scene = String(n);
  tour.chapters.children[n - 1].scrollIntoView({ block: "nearest" });
}

function tourState(playing) {
  tour.root.classList.toggle("paused", !playing);
  const [name, label, aria] = tour.ended ? ["rotate-ccw", "Revoir", "Revoir depuis le début"]
    : playing ? ["pause", "Pause", "Mettre en pause"] : ["play", "Lecture", "Reprendre la lecture"];
  tour.play.querySelector("use").setAttribute("href", "icons.svg#" + name);
  tour.play.querySelector("span").textContent = label;
  tour.play.setAttribute("aria-label", aria);
}

function startTour() {
  tour.ended = false;
  tourScene(1);
  tourState(!tour.reduced.matches);
}

tour.chapters.addEventListener("animationend", e => {
  if (e.animationName !== "progress") { return; }
  if (tour.scene < tour.chapters.children.length) {
    tourScene(tour.scene + 1);
  } else {
    tour.chapters.lastElementChild.dataset.done = "1";
    tour.ended = true;
    tourState(false);
  }
});

tour.chapters.addEventListener("click", e => {
  const b = e.target.closest("button[data-scene]");
  if (!b) { return; }
  tour.ended = false;
  tourScene(Number(b.dataset.scene));
  tourState(!tour.reduced.matches && !tour.root.classList.contains("paused"));
});

tour.play.addEventListener("click", () => {
  if (tour.ended) { startTour(); return; }
  tourState(tour.root.classList.contains("paused"));
});

// ------------------------------------------------------------------ historique
async function loadHistory() {
  try {
    const h = (await api("/api/history")).body;
    el("history").replaceChildren(...h.events.map(logItem));
    el("history-empty").hidden = h.events.length > 0;
    drawChart(h.metrics);
  } catch (err) { feedback(false, "Historique illisible (" + err.message + ")."); }
}

function drawChart(points) {
  const svg = el("chart");
  const ns = "http://www.w3.org/2000/svg";
  svg.replaceChildren();
  el("chart-empty").hidden = points.length >= 2;
  el("chart-box").hidden = points.length < 2;
  if (points.length < 2) { return; }
  const W = 600, H = 260, pad = 22;
  const t0 = Date.parse(points[0].at), t1 = Date.parse(points[points.length - 1].at);
  const maxRam = Math.max(...points.map(p => p.ramAvailGB), 1);
  const maxDisk = Math.max(...points.map(p => p.diskFreeGB), 1);
  const x = p => pad + (W - 2 * pad) * (t1 === t0 ? 0 : (Date.parse(p.at) - t0) / (t1 - t0));
  const y = (v, max) => H - pad - (H - 2 * pad) * (v / max);
  for (let i = 0; i <= 4; i++) {
    const line = document.createElementNS(ns, "line");
    const yy = pad + (H - 2 * pad) * i / 4;
    line.setAttribute("x1", pad); line.setAttribute("x2", W - pad); line.setAttribute("y1", yy); line.setAttribute("y2", yy);
    line.setAttribute("class", "grid-line");
    svg.append(line);
  }
  el("chart-start").textContent = dayTime(points[0].at);
  el("chart-end").textContent = dayTime(points[points.length - 1].at);
  el("key-ram").textContent = "Mémoire disponible, max " + gb(maxRam);
  el("key-disk").textContent = "Disque libre, max " + gb(maxDisk);
  for (const [cls, key, max] of [["ram", "ramAvailGB", maxRam], ["disk", "diskFreeGB", maxDisk]]) {
    const pl = document.createElementNS(ns, "polyline");
    pl.setAttribute("class", cls);
    pl.setAttribute("points", points.map(p => x(p).toFixed(1) + "," + y(p[key], max).toFixed(1)).join(" "));
    svg.append(pl);
  }
}

// ------------------------------------------------------------------ cycle de rafraichissement
async function refresh() {
  try {
    const r = await api("/api/state");
    renderState(r.body);
  } catch (err) {
    const health = el("health");
    health.textContent = "FreePC ne répond pas (" + err.message + ")";
    health.dataset.level = "crit";
  }
}

(async function init() {
  try {
    const h = (await api("/api/health")).body;
    el("version").textContent = "v" + h.version;
    el("foot-version").textContent = "v" + h.version;
  } catch { /* la version n'est qu'informative */ }
  showView(location.hash.slice(1) || "dashboard");
  await refresh();
  setInterval(refresh, REFRESH_MS);
})();
