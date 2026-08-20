// Content renders FROM THE REPO, never a copy [D:site-skeleton] — the
// loaders point at ../docs and the repo root, so a docs edit is a site
// edit with no sync step. No schema: docs/*.md are written for GitHub and
// carry no frontmatter; requiring any would invert that decision.
// DECISIONS.md is deliberately not loaded — it is the maintainers' ledger,
// not a docs page; links to it rewrite to GitHub (rewrite-doc-links.mjs
// mirrors this set).
import { defineCollection } from "astro:content";
import { glob } from "astro/loaders";

const docs = defineCollection({
  loader: glob({
    pattern: ["*.md", "!DECISIONS.md"],
    base: "../docs",
  }),
});

// the one root-level doc the site renders: its audience is existing
// users deciding whether to upgrade — a docs page, not a front page
const changelog = defineCollection({
  loader: glob({ pattern: "CHANGELOG.md", base: ".." }),
});

export const collections = { docs, changelog };
