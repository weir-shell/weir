// The postbuild search guard: a broken postbuild must FAIL the build,
// never ship a dead search box. Three assertions on the built dist —
// the Pagefind bundle exists, the member index the modal actually
// fetch-loads (the emitted reference.json asset, located via the
// modal's own data-ref-url in the built HTML) parses non-empty, and a
// prose search lands on a section anchor.
// Node, not weir: this runs inside the site's npm toolchain in the
// deploy job, which has no weir binary at build time.
import { existsSync, readFileSync } from "node:fs";
import { rankSections } from "../src/lib/rank-sections.mjs";
import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const fail = (msg) => {
  console.error(`search guard: ${msg}`);
  process.exit(1);
};

if (!existsSync("dist/pagefind/pagefind.js"))
  fail("dist/pagefind/pagefind.js missing — pagefind did not index the site");

const html = readFileSync("dist/index.html", "utf8");
const m = html.match(/data-ref-url="([^"]+)"/);
if (!m) fail("no data-ref-url in dist/index.html — the search modal is not in the layout");

let ref;
try {
  ref = JSON.parse(readFileSync(`dist${m[1]}`, "utf8"));
} catch (e) {
  fail(`member index dist${m[1]} unreadable: ${e.message}`);
}
const count =
  (ref.modules ?? []).reduce((n, mod) => n + mod.members.length, 0) +
  (ref.forms ?? []).length +
  (ref.types ?? []).length;
if (count === 0) fail(`member index dist${m[1]} parsed empty`);

console.log(`search guard: pagefind bundle present, member index has ${count} entries`);

// The section-ranking guard: prose hits must land on a heading anchor,
// not a page top — searching "Types" must put the reference's Types
// section first. Runs the shipped rankSections against the built index
// (fetch reads the dist files; Pagefind's own loader is browser-shaped).

globalThis.fetch = async (u) => new Response(await readFile(new URL(String(u))));
const base = pathToFileURL(`${process.cwd()}/dist/pagefind/`).href;
const pagefind = await import(`${base}pagefind.js`);
await pagefind.options({ basePath: base });
await pagefind.init();
const res = await pagefind.search("Types");
const pages = await Promise.all(res.results.map((r) => r.data()));
const top = rankSections(pages, "Types", 1)[0];
if (!top?.url.endsWith("/reference/#types"))
  fail(`searching "Types" ranks ${top?.url ?? "nothing"} first, not the reference's #types section`);

console.log(`search guard: "Types" ranks ${top.url.replace(/^.*\/dist/, "")} first`);
