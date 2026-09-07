// The site build [D:site-skeleton] — Astro, because the highlighting
// ruling reuses the EXISTING tmLanguage grammar: Shiki loads a TextMate
// grammar object directly and runs at build time shipping zero JS, so the
// site's colours come from the same grammar ci/e2e.sh already gates
// across micro/tmLanguage/tree-sitter. A generator with its own
// highlighter (syntect/Chroma) would mean maintaining a fourth grammar.
import { defineConfig } from "astro/config";
import { unified } from "@astrojs/markdown-remark";
import { readFileSync } from "node:fs";
import { rewriteDocLinks } from "./src/remark/rewrite-doc-links.mjs";
import queilThemes from "./src/lib/queil-weir.mjs";

// the ONE grammar, read from the repo — never a copy
const weirGrammar = JSON.parse(
  readFileSync(
    new URL("../editors/vscode/syntaxes/weir.tmLanguage.json", import.meta.url),
    "utf8",
  ),
);

// the per-module pages retired once the one-page reference grew its
// side navigation — links in the wild keep working via redirects into
// the same anchors the pages used
const refData = JSON.parse(
  readFileSync(new URL("./src/data/reference.json", import.meta.url), "utf8"),
);
const moduleRedirects = Object.fromEntries([
  // module-listing anchors carry the module- prefix (the written
  // pages' headings own the bare names — the dup-anchor fix)
  ...refData.modules.map((m) => [
    `/reference/${m.name.toLowerCase()}`,
    `/reference/#module-${m.name.toLowerCase()}`,
  ]),
  ["/reference/forms", "/reference/#module-forms"],
  ["/reference/all", "/reference/"],
  ["/reference/lexical", "/reference/#lexical"],
  // the tooling sub-pages merged into one page with a side nav
  ["/docs/cli", "/docs/tooling/#the-cli"],
  ["/docs/signatures", "/docs/tooling/#command-signatures"],
  ["/docs/schemas", "/docs/tooling/#yaml-schemas"],
  ["/docs/project", "/docs/tooling/#project-layout-weir"],
  ["/docs/configuration", "/docs/tooling/#configuration"],
]);

export default defineConfig({
  site: "https://weir.sh",
  // compressHTML OFF [D:site-skeleton]: the default minifier strips the
  // whitespace-only gap between prose text and an inline element, so a
  // line-broken `The <code>///</code>` rendered as `The<code>///</code>`
  // — space eaten. Off, the source whitespace renders as authored (the
  // intended prose spacing); code lives in <pre>, unaffected either way.
  // Do NOT re-enable without restoring explicit spaces at every text↔
  // inline-element line break on index.astro.
  compressHTML: false,
  redirects: moduleRedirects,
  markdown: {
    shikiConfig: {
      // `weir-error` fences are teaching blocks (SKILL/GUIDE convention):
      // same grammar, they just must not fall back to plaintext
      // weir-demo: the same grammar for DISPLAY-ONLY blocks — shapes
      // that need a live endpoint or a real token; skill-doc extracts
      // only weir/weir-error, so these are highlighted, never executed
      langs: [weirGrammar, { ...weirGrammar, name: "weir-error" }, { ...weirGrammar, name: "weir-demo" }],
      themes: queilThemes,
      defaultColor: false,
    },
    // Astro 7 [D:site-skeleton]: remark plugins ride the unified()
    // processor (the markdown-level remarkPlugins key is deprecated);
    // shikiConfig stays at this level — the processor's renderer reads it
    processor: unified({ remarkPlugins: [rewriteDocLinks] }),
  },
});
