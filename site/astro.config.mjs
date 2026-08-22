// The site build [D:site-skeleton] — Astro, because the highlighting
// ruling reuses the EXISTING tmLanguage grammar: Shiki loads a TextMate
// grammar object directly and runs at build time shipping zero JS, so the
// site's colours come from the same grammar ci/e2e.sh already gates
// across micro/tmLanguage/tree-sitter. A generator with its own
// highlighter (syntect/Chroma) would mean maintaining a fourth grammar.
import { defineConfig } from "astro/config";
import { readFileSync } from "node:fs";
import { rewriteDocLinks } from "./src/remark/rewrite-doc-links.mjs";
import nordWeir from "./src/lib/nord-weir.mjs";

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
  ...refData.modules.map((m) => [
    `/reference/${m.name.toLowerCase()}`,
    `/reference/all/#${m.name.toLowerCase()}`,
  ]),
  ["/reference/forms", "/reference/all/#forms"],
]);

export default defineConfig({
  site: "https://weir.sh",
  redirects: moduleRedirects,
  markdown: {
    shikiConfig: {
      // `weir-error` fences are teaching blocks (SKILL/GUIDE convention):
      // same grammar, they just must not fall back to plaintext
      langs: [weirGrammar, { ...weirGrammar, name: "weir-error" }],
      theme: nordWeir,
    },
    remarkPlugins: [rewriteDocLinks],
  },
});
