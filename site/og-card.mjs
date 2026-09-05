// Renders public/og.png — the link-preview card [D:og-card]: the mark,
// the tagline, and beat 1's GENUINE refusal (first lines verbatim,
// tail elided with an ellipsis — never a mock-up), drawn in the site's
// own dark palette and the shipped Iosevka. Not part of the build; the
// PNG is committed. Re-run after a palette, mark, or beat-1 change:
//
//   cd site && npm install --no-save wawoff2 @resvg/resvg-js
//   node og-card.mjs
import { Resvg } from "@resvg/resvg-js";
import wawoff2 from "wawoff2";
import fs from "node:fs";
import { fileURLToPath } from "node:url";

const here = (p) => fileURLToPath(new URL(p, import.meta.url));

// html.dark's tokens (Base.astro) + the mark's dark set (favicon.svg)
const P = {
  bg: "#0e1113",
  fg: "#d6dade",
  muted: "#98a2ac",
  accent: "#7fb2f0",
  codeBg: "#161b1e",
  border: "#2a3138",
  danger: "#ff8098",
  b1: "#7fb2f0",
  b2: "#4fc1e8",
  b3: "#2fc6c6",
};

const mark = (x, y, s) => `
  <g transform="translate(${x},${y}) scale(${s})">
    <rect fill="${P.b1}" x="20" y="34" width="56" height="12" rx="6"/>
    <rect fill="${P.b2}" x="36" y="57" width="56" height="12" rx="6"/>
    <rect fill="${P.b3}" x="52" y="80" width="56" height="12" rx="6"/>
  </g>`;

const term = [
  { t: "$ weir release.weir", c: P.muted },
  { t: "release.weir:8:24: parse error:", c: P.fg },
  { t: "if not cli.dryRun then rsnyc -av bundle.tar.gz backup:/srv/dist", c: P.fg },
  { t: "                       ^", c: P.danger },
  { t: "unknown command 'rsnyc' — not found on PATH. weir resolves", c: P.danger },
  { t: "command names before running…  (nothing ran — tar included)", c: P.danger },
];

const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const termY = 336;
const lineH = 34;

const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630">
  <rect width="1200" height="630" fill="${P.bg}"/>
  ${mark(64, 58, 2.1)}
  <text x="330" y="188" font-family="Iosevka Term Ext" font-weight="700" font-size="120" fill="${P.fg}">weir</text>
  <text x="64" y="290" font-family="Iosevka Term Ext" font-size="40" fill="${P.muted}">a typed shell-scripting language</text>
  <rect x="64" y="${termY}" width="1072" height="${term.length * lineH + 44}" rx="10" fill="${P.codeBg}" stroke="${P.border}"/>
  <rect x="64" y="${termY}" width="5" height="${term.length * lineH + 44}" fill="${P.danger}"/>
  ${term
    .map(
      (l, i) =>
        `<text x="96" y="${termY + 44 + i * lineH}" font-family="Iosevka Term Ext" font-size="25" xml:space="preserve" fill="${l.c}">${esc(l.t)}</text>`,
    )
    .join("\n  ")}
  <text x="1136" y="600" text-anchor="end" font-family="Iosevka Term Ext" font-size="28" fill="${P.accent}">weir.sh</text>
</svg>`;

// decompressed to FILES, not buffers — resvg's fontBuffers path shaped
// punctuation wrong (raised periods); fontFiles renders correctly
import os from "node:os";
import path from "node:path";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "og-fonts-"));

const ttf = async (p, name) => {
  const out = path.join(tmp, name);
  fs.writeFileSync(out, Buffer.from(await wawoff2.decompress(fs.readFileSync(here(p)))));
  return out;
};

const fonts = [
  await ttf("public/fonts/iosevka-term-ext-regular.woff2", "regular.ttf"),
  await ttf("public/fonts/iosevka-term-ext-bold.woff2", "bold.ttf"),
];

const png = new Resvg(svg, {
  fitTo: { mode: "width", value: 1200 },
  font: { fontFiles: fonts, loadSystemFonts: false, defaultFontFamily: "Iosevka Term Ext" },
})
  .render()
  .asPng();

fs.rmSync(tmp, { recursive: true, force: true });

fs.writeFileSync(here("public/og.png"), png);
console.log("public/og.png:", png.length, "bytes");
