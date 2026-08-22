// Relative-link rewriting [D:site-skeleton] — docs/*.md are written for
// GitHub (`[x](GUIDE.md#anchor)`, `[y](../SECURITY.md)`) and render here
// unedited, so the SITE adapts to the files, never the reverse. Three
// rules, applied to every relative link ending in .md:
//   1. a doc this site renders        -> /docs/<slug>/#anchor
//   2. CHANGELOG.md                   -> /changelog/
//   3. anything else in the repo      -> the GitHub blob URL
// Rule 3 covers DECISIONS.md (the maintainers' ledger — 557KB of index
// rows is not a docs page), SECURITY.md, tests/fidelity/divergences.md:
// real files a reader may want, just not site pages.
import { readdirSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const GITHUB = "https://github.com/weir-shell/weir/blob/main";

// the rendered set mirrors content.config.ts's globs — DECISIONS.md is
// deliberately absent from both
const RENDERED = new Set([
  "docs/GUIDE.md",
  "docs/METHOD.md",
  "docs/INSTALL.md",
  "docs/COMING-FROM.md",
  "docs/LEXICON.md",
  "docs/editors.md",
  "docs/repl.md",
  "docs/tooling.md",
]);

// the mirror is CHECKED, not commented: a docs page the glob renders but
// this set does not know would silently GitHub-link — the drift mode the
// guide reorder hit with repl.md. Throws at build, both directions.
const EXCLUDED = new Set(["docs/DECISIONS.md", "docs/SEMANTICS.md"]);
const onDisk = new Set(
  readdirSync(resolve(repoRoot, "docs"))
    .filter((f) => f.endsWith(".md"))
    .map((f) => `docs/${f}`),
);
for (const f of onDisk) {
  if (!RENDERED.has(f) && !EXCLUDED.has(f))
    throw new Error(`rewrite-doc-links: '${f}' is rendered by content.config but missing from RENDERED — its links would silently point at GitHub`);
}
for (const f of RENDERED) {
  if (!onDisk.has(f))
    throw new Error(`rewrite-doc-links: RENDERED lists '${f}' but docs/ has no such file`);
}

const slugOf = (repoPath) =>
  repoPath.replace(/^docs\//, "").replace(/\.md$/, "").toLowerCase();

function walk(node, fn) {
  fn(node);
  for (const child of node.children ?? []) walk(child, fn);
}

export function rewriteDocLinks() {
  return (tree, file) => {
    walk(tree, (node) => {
      if (node.type !== "link" && node.type !== "definition") return;
      const url = node.url ?? "";
      // absolute URLs, in-page anchors, non-md targets: untouched
      if (/^[a-z]+:/i.test(url) || url.startsWith("#")) return;
      const [target, anchor] = url.split("#");
      if (!target.endsWith(".md")) return;

      const sourceDir = file?.path ? dirname(file.path) : resolve(repoRoot, "docs");
      const repoPath = relative(repoRoot, resolve(sourceDir, target)).replaceAll("\\", "/");
      const suffix = anchor ? `#${anchor}` : "";

      if (RENDERED.has(repoPath)) {
        node.url = `/docs/${slugOf(repoPath)}/${suffix}`;
      } else if (repoPath.startsWith("docs/reference/")) {
        // the reference is ONE page: a link's own anchor wins, else the
        // page lands at its title section
        const slug = slugOf(repoPath.replace("docs/reference/", "docs/"));
        node.url = anchor ? `/reference/${suffix}` : `/reference/#${slug}`;
      } else if (repoPath === "CHANGELOG.md") {
        node.url = `/changelog/${suffix}`;
      } else if (!repoPath.startsWith("..")) {
        node.url = `${GITHUB}/${repoPath}${suffix}`;
      }
      // a target escaping the repo entirely is left alone — nothing
      // sensible to rewrite it to
    });
  };
}
