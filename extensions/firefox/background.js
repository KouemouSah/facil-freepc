"use strict";

/* global FreePCPolicy, browser */

// Verification toutes les 5 minutes : met en veille les onglets choisis par la politique (policy.js).
const ALARM = "freepc-discard";
const PERIOD_MINUTES = 5;

async function settings() {
  const stored = await browser.storage.local.get(["enabled", "inactivityMinutes"]);
  return FreePCPolicy.normalizeSettings(stored);
}

async function sweep() {
  const tabs = await browser.tabs.query({});
  const ids = FreePCPolicy.selectTabsToDiscard(tabs, Date.now(), await settings());
  if (ids.length === 0) {
    return;
  }
  try {
    await browser.tabs.discard(ids);
  } catch (e) {
    // un onglet ferme entre-temps fait echouer le lot : on retente onglet par onglet
    for (const id of ids) {
      try {
        await browser.tabs.discard(id);
      } catch (ignored) {
        /* onglet disparu ou non dechargeable : ignore */
      }
    }
  }
  const total = ((await browser.storage.local.get("discardedTotal")).discardedTotal || 0) + ids.length;
  await browser.storage.local.set({ discardedTotal: total, lastSweepAt: Date.now() });
}

browser.alarms.create(ALARM, { periodInMinutes: PERIOD_MINUTES });
browser.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === ALARM) {
    sweep();
  }
});
