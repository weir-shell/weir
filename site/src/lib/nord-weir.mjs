// The site's ONE theme [D:site-design]: Shiki's bundled Nord, with two
// stated patches — never a hand-authored theme.
//   1. comments: stock #616e88 on nord0 measures 2.44:1, far under the
//      AA floor (github-dark sat at ~4.6:1); lifted to #8fa3bd = 4.84:1.
//   2. entity.name.type: Nord styles entity.name.type.class but not the
//      parent scope, so weir's module/type names (Seq, Path) fell back
//      to default fg — given nord7, the colour Nord's own class rule
//      uses. A scope-mapping gap in the theme, not the grammar.
import nord from "@shikijs/themes/nord";

const theme = structuredClone(nord);
theme.name = "nord-weir";

for (const tc of theme.tokenColors ?? []) {
  if (tc.settings?.foreground?.toLowerCase() === "#616e88") {
    tc.settings.foreground = "#8fa3bd";
  }
}

theme.tokenColors.push({
  scope: "entity.name.type",
  settings: { foreground: "#8FBCBB" },
});

export default theme;
