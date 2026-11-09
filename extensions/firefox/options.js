"use strict";

/* global FreePCPolicy, browser */

const enabled = document.getElementById("enabled");
const minutes = document.getElementById("minutes");

async function load() {
  const stored = await browser.storage.local.get(["enabled", "inactivityMinutes", "discardedTotal", "lastSweepAt"]);
  const s = FreePCPolicy.normalizeSettings(stored);
  enabled.checked = s.enabled;
  minutes.value = s.inactivityMinutes;
  const total = stored.discardedTotal || 0;
  document.getElementById("stats").textContent = stored.lastSweepAt
    ? `${total} onglet(s) mis en veille depuis l'installation ; dernière vérification : ${new Date(stored.lastSweepAt).toLocaleString("fr-FR")}.`
    : "Première vérification dans quelques minutes.";
}

async function save() {
  const s = FreePCPolicy.normalizeSettings({ enabled: enabled.checked, inactivityMinutes: minutes.value });
  await browser.storage.local.set(s);
  minutes.value = s.inactivityMinutes;   // valeur ramenee dans les bornes si besoin
  document.getElementById("saved").textContent = "Réglages enregistrés.";
}

enabled.addEventListener("change", save);
minutes.addEventListener("change", save);
load();
