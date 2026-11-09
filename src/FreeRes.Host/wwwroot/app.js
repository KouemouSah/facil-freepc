"use strict";

// Tableau de bord minimal (phase 1) : etat reel du moteur, rafraichi toutes les 10 s.
// Tout le texte est insere via textContent (jamais innerHTML) : aucune injection possible depuis les donnees.
const REFRESH_MS = 10000;
const nf1 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 1 });
const nf2 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 2 });
const SEVERITY = { Crit: "Critique", Warn: "Alerte", Info: "Info" };

function el(id) { return document.getElementById(id); }

// level : "ok" | "warn" | "crit"
function setGauge(id, value, fillPct, level, sub) {
  const g = el(id);
  g.querySelector("[data-v]").textContent = value;
  const bar = g.querySelector("[data-b]");
  bar.style.width = Math.max(0, Math.min(100, fillPct)).toFixed(0) + "%";
  g.dataset.level = level;
  g.querySelector("[data-s]").textContent = sub;
}

function levelLow(v, warn, crit) { return v < crit ? "crit" : v < warn ? "warn" : "ok"; }
function levelHigh(v, warn, crit) { return v >= crit ? "crit" : v >= warn ? "warn" : "ok"; }

function render(s) {
  const t = s.thresholds, m = s.snapshot, p = s.profile;

  setGauge("g-ram", nf1.format(m.ramAvailGB) + " GB",
    100 * m.ramAvailGB / m.ramTotalGB, levelLow(m.ramAvailGB, t.ramAvailWarnGB, t.ramAvailCritGB),
    "sur " + nf1.format(m.ramTotalGB) + " GB · alerte < " + nf1.format(t.ramAvailWarnGB) + " GB");
  setGauge("g-commit", nf1.format(m.commitPct) + " %",
    m.commitPct, levelHigh(m.commitPct, t.commitWarnPct, t.commitCritPct),
    nf1.format(m.commitUsedGB) + " / " + nf1.format(m.commitLimitGB) + " GB (RAM + fichier d'échange)");
  setGauge("g-disk", nf1.format(m.systemDriveFreeGB) + " GB",
    100 * m.systemDriveFreeGB / m.systemDriveTotalGB, levelLow(m.systemDriveFreeGB, t.diskFreeWarnGB, t.diskFreeCritGB),
    m.systemDrive + " sur " + nf1.format(m.systemDriveTotalGB) + " GB · alerte < " + nf1.format(t.diskFreeWarnGB) + " GB");

  const top = el("top");
  top.replaceChildren(...m.topProcesses.slice(0, 6).map(pr => {
    const li = document.createElement("li");
    const name = document.createElement("span");
    name.textContent = pr.name + (pr.count > 1 ? " (" + pr.count + ")" : "");
    const v = document.createElement("span");
    v.className = "num";
    v.textContent = nf2.format(pr.workingSetGB) + " GB";
    li.append(name, v);
    return li;
  }));

  const list = el("pending");
  list.replaceChildren(...s.pending.map(d => {
    const li = document.createElement("li");
    li.dataset.severity = d.severity;
    const sev = document.createElement("span");
    sev.className = "sev";
    sev.textContent = SEVERITY[d.severity] || d.severity;
    const title = document.createElement("strong");
    title.textContent = d.title;
    const detail = document.createElement("p");
    detail.className = "muted small";
    detail.textContent = d.detail + (d.estGainGB > 0 ? " · gain estimé ~" + nf2.format(d.estGainGB) + " GB" : "");
    li.append(sev, title, detail);
    return li;
  }));
  el("pending-count").textContent = s.pending.length;
  el("pending-empty").hidden = s.pending.length > 0;

  const health = el("health");
  health.textContent = s.critical ? "État critique" : "Surveillance active";
  health.dataset.level = s.critical ? "crit" : "ok";

  el("machine").textContent = [p.manufacturer, p.model].filter(Boolean).join(" ") + " · " + p.osName +
    (p.osDisplayVersion ? " " + p.osDisplayVersion : "") + " (build " + p.osBuild + ") · " + p.logicalProcessors + " processeurs logiques";
  el("cycle").textContent = s.lastCycle
    ? "Dernière analyse : " + new Date(s.lastCycle.at).toLocaleTimeString("fr-FR") + " · toutes les " + s.monitorIntervalMinutes + " min" +
      (s.lastCycle.errors.length ? " · " + s.lastCycle.errors.length + " erreur(s) de module" : "")
    : "Première analyse en cours…";

  const warnings = s.settingsErrors || [];
  el("warnings").hidden = warnings.length === 0;
  el("warning-list").replaceChildren(...warnings.map(w => { const li = document.createElement("li"); li.textContent = w; return li; }));
}

async function refresh() {
  try {
    const res = await fetch("/api/state", { credentials: "same-origin", cache: "no-store" });
    if (!res.ok) { throw new Error("HTTP " + res.status); }
    render(await res.json());
  } catch (e) {
    const health = el("health");
    health.textContent = "Erreur : " + e.message;
    health.dataset.level = "crit";
  }
}

(async function init() {
  try {
    const h = await (await fetch("/api/health", { credentials: "same-origin" })).json();
    el("version").textContent = "v" + h.version;
  } catch { /* la version n'est qu'informative */ }
  await refresh();
  setInterval(refresh, REFRESH_MS);
})();
