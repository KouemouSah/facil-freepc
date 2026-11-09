"use strict";

// Tests de la politique de mise en veille : node --test extensions/firefox/test
const test = require("node:test");
const assert = require("node:assert/strict");
const P = require("../policy.js");

const NOW = Date.UTC(2026, 10, 9, 12, 0, 0);
const MIN = 60 * 1000;
const tab = (id, over) => Object.assign({ id, url: "https://exemple.fr/", active: false, pinned: false, audible: false, discarded: false, lastAccessed: NOW - 60 * MIN }, over);

test("met en veille un onglet web inactif depuis plus que le delai", () => {
  assert.deepEqual(P.selectTabsToDiscard([tab(1)], NOW, {}), [1]);
});

test("jamais l'onglet actif, epingle, qui joue du son ou deja en veille", () => {
  const tabs = [tab(1, { active: true }), tab(2, { pinned: true }), tab(3, { audible: true }), tab(4, { discarded: true }), tab(5)];
  assert.deepEqual(P.selectTabsToDiscard(tabs, NOW, {}), [5]);
});

test("jamais les pages internes de Firefox ou d'extension", () => {
  const tabs = [tab(1, { url: "about:preferences" }), tab(2, { url: "moz-extension://x/options.html" }), tab(3, { url: "file:///C:/a.html" }), tab(4, { url: "http://intranet/" })];
  assert.deepEqual(P.selectTabsToDiscard(tabs, NOW, {}), [4]);
});

test("respecte le delai d'inactivite (limite incluse)", () => {
  const tabs = [tab(1, { lastAccessed: NOW - 29 * MIN }), tab(2, { lastAccessed: NOW - 30 * MIN })];
  assert.deepEqual(P.selectTabsToDiscard(tabs, NOW, { inactivityMinutes: 30 }), [2]);
});

test("desactive : rien n'est mis en veille", () => {
  assert.deepEqual(P.selectTabsToDiscard([tab(1)], NOW, { enabled: false }), []);
});

test("reglages hors bornes ramenes a des valeurs sures", () => {
  assert.equal(P.normalizeSettings({ inactivityMinutes: 1 }).inactivityMinutes, P.MIN_MINUTES);
  assert.equal(P.normalizeSettings({ inactivityMinutes: 999999 }).inactivityMinutes, P.MAX_MINUTES);
  assert.equal(P.normalizeSettings({ inactivityMinutes: "abc" }).inactivityMinutes, 30);
  assert.equal(P.normalizeSettings(undefined).enabled, true);
});

test("onglet sans date d'acces ou entree invalide : jamais selectionne", () => {
  assert.deepEqual(P.selectTabsToDiscard([tab(1, { lastAccessed: undefined }), null], NOW, {}), []);
  assert.deepEqual(P.selectTabsToDiscard(null, NOW, {}), []);
});
