"""Construit src/FreeRes.Host/wwwroot/icons.svg (sprite de symboles) a partir du paquet lucide-static extrait.

Usage : python tools/New-IconSprite.py <dossier package de lucide-static>
Seules les icones listees sont incluses ; la licence complete (ISC Lucide + MIT Feather) est recopiee en tete.
"""
import pathlib
import re
import sys

ICONS = [
    # navigation
    "layout-dashboard", "app-window", "globe", "brain-circuit", "container", "history", "circle-help",
    # mesures et etats
    "memory-stick", "layers", "hard-drive", "activity", "power", "zap", "bell",
    # decisions
    "check", "clock", "x", "refresh-cw", "shield", "triangle-alert", "circle-alert", "info", "circle-check",
    # animation « Comment ca marche »
    "gauge", "search", "lightbulb", "mouse-pointer-click", "sparkles", "shield-check", "scroll-text",
    "play", "pause", "rotate-ccw", "cpu",
]


def main():
    pkg = pathlib.Path(sys.argv[1])
    version = re.search(r'"version":\s*"([^"]+)"', (pkg / "package.json").read_text(encoding="utf-8")).group(1)
    # Licence COMPLETE : ISC de Lucide + MIT de Feather (check, clock, info, power, search, x en derivent).
    license_text = (pkg / "LICENSE").read_text(encoding="utf-8").strip()
    out = [
        '<svg xmlns="http://www.w3.org/2000/svg">',
        f"<!-- Icones Lucide v{version} (https://lucide.dev), licence ci-dessous :",
        re.sub(r"-{2,}", lambda m: " ".join("-" * len(m.group(0))), license_text),   # « -- » interdit dans un commentaire XML
        "-->",
    ]
    for name in ICONS:
        svg = (pkg / "icons" / f"{name}.svg").read_text(encoding="utf-8")
        body = re.search(r"<svg[^>]*>(.*)</svg>", svg, re.S).group(1)
        body = " ".join(line.strip() for line in body.strip().splitlines())
        out.append(f'<symbol id="{name}" viewBox="0 0 24 24">{body}</symbol>')
    out.append("</svg>")
    target = pathlib.Path(__file__).resolve().parent.parent / "src" / "FreeRes.Host" / "wwwroot" / "icons.svg"
    target.write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(ICONS)} icones -> {target}")


main()
