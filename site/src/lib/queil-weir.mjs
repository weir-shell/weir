// The site's code themes [D:queil-theme] — a PORT of queil.net's Chroma
// palette (a custom variant of the classic Pygments/GitHub palette; no
// Shiki-bundled equivalent exists) onto the TextMate scopes weir's
// grammar emits, plus a DERIVED dark variant: hue kept, lightness and
// saturation re-tuned per token against the dark ground — an invention,
// not a port; the two sites are not expected to match in dark mode.
//
// AA lifts in the light port, hue kept, each measured on #f4f4f4:
//   comments #999988→#6e6e64 (2.63→4.68), numbers #009999→#007575
//   (3.17→5.02), punctuation #5e81ac→#44648c (3.66→5.54), builtins
//   #0086b3→#00658a (3.77→5.91). Strings #dd1144 pass at 4.51 as-is.
// Every dark token measures ≥5:1 on #161b1e.

const scopes = (rules) =>
  rules.map(([scope, foreground, fontStyle]) => ({
    scope,
    settings: { foreground, ...(fontStyle ? { fontStyle } : {}) },
  }));

export const light = {
  name: "queil-weir-light",
  type: "light",
  colors: { "editor.background": "#f4f4f4", "editor.foreground": "#445588" },
  tokenColors: scopes([
    [["comment", "punctuation.definition.comment"], "#6e6e64", "italic"],
    [["keyword", "keyword.control", "keyword.operator", "keyword.other", "storage"], "#111111"],
    [["constant.language"], "#111111", "bold"],
    [["string", "string.quoted", "string.interpolated"], "#dd1144"],
    [["constant.character", "constant.character.escape"], "#dd1144", "bold"],
    [["meta.interpolation", "punctuation.section", "punctuation.definition"], "#44648c"],
    [["constant.numeric"], "#007575"],
    [["entity.name.type", "entity.name.class", "support.class", "support.type"], "#004396", "bold"],
    [["entity.name.function", "support.function"], "#006680"],
    [["variable", "variable.other"], "#005a80"],
    [["support", "entity.name.tag"], "#00658a"],
  ]),
};

export const dark = {
  name: "queil-weir-dark",
  type: "dark",
  colors: { "editor.background": "#161b1e", "editor.foreground": "#a3b1d6" },
  tokenColors: scopes([
    [["comment", "punctuation.definition.comment"], "#8b8b7a", "italic"],
    [["keyword", "keyword.control", "keyword.operator", "keyword.other", "storage"], "#e2e4e8"],
    [["constant.language"], "#e2e4e8", "bold"],
    [["string", "string.quoted", "string.interpolated"], "#ff8098"],
    [["constant.character", "constant.character.escape"], "#ff8098", "bold"],
    [["meta.interpolation", "punctuation.section", "punctuation.definition"], "#81a1c1"],
    [["constant.numeric"], "#2fc6c6"],
    [["entity.name.type", "entity.name.class", "support.class", "support.type"], "#7fb2f0", "bold"],
    [["entity.name.function", "support.function"], "#4fb3d0"],
    [["variable", "variable.other"], "#5cc0e8"],
    [["support", "entity.name.tag"], "#4fc1e8"],
  ]),
};

export const themes = { light, dark };
export default themes;
