"use strict";

/*
 * Politique de mise en veille (fonction PURE, testee sous Node : test/policy.test.cjs).
 * Un onglet est mis en veille seulement s'il est :
 *   - inactif depuis au moins `inactivityMinutes` (lastAccessed) ;
 *   - ni l'onglet actif de sa fenetre, ni epingle, ni en train de jouer du son, ni deja en veille ;
 *   - une page web (http/https) : jamais les pages internes de Firefox ni celles des extensions.
 * Les reglages hors bornes sont ramenes aux valeurs sures.
 */
const FreePCPolicy = (() => {
  const DEFAULTS = Object.freeze({ enabled: true, inactivityMinutes: 30 });
  const MIN_MINUTES = 5;
  const MAX_MINUTES = 24 * 60;

  function normalizeSettings(raw) {
    const s = Object.assign({}, DEFAULTS, raw || {});
    const minutes = Number(s.inactivityMinutes);
    return {
      enabled: s.enabled !== false,
      inactivityMinutes: Number.isFinite(minutes)
        ? Math.min(MAX_MINUTES, Math.max(MIN_MINUTES, Math.round(minutes)))
        : DEFAULTS.inactivityMinutes,
    };
  }

  function isWebPage(url) {
    return typeof url === "string" && /^https?:\/\//i.test(url);
  }

  function selectTabsToDiscard(tabs, nowMs, rawSettings) {
    const settings = normalizeSettings(rawSettings);
    if (!settings.enabled || !Array.isArray(tabs)) {
      return [];
    }
    const limit = nowMs - settings.inactivityMinutes * 60 * 1000;
    return tabs
      .filter((t) => t && !t.active && !t.pinned && !t.audible && !t.discarded)
      .filter((t) => isWebPage(t.url))
      .filter((t) => typeof t.lastAccessed === "number" && t.lastAccessed <= limit)
      .map((t) => t.id);
  }

  return Object.freeze({ DEFAULTS, MIN_MINUTES, MAX_MINUTES, normalizeSettings, selectTabsToDiscard });
})();

if (typeof module !== "undefined" && module.exports) {
  module.exports = FreePCPolicy;
}
