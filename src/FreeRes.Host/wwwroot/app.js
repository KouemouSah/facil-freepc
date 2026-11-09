"use strict";

// Phase 0 : verifie que le serveur local repond et affiche son identite.
(async function () {
  const status = document.getElementById("status");
  try {
    const res = await fetch("/api/health", { credentials: "same-origin" });
    if (!res.ok) {
      throw new Error("HTTP " + res.status);
    }
    const h = await res.json();
    document.getElementById("version").textContent = "v" + h.version;
    document.getElementById("machine").textContent = h.machine;
    document.getElementById("started").textContent = new Date(h.startedAt).toLocaleString("fr-FR");
    status.textContent = "Connecté";
    status.className = "ok";
  } catch (e) {
    status.textContent = "Erreur : " + e.message;
    status.className = "err";
  }
})();
