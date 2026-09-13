// The postbuild search guard: a broken postbuild must FAIL the build,
// never ship a dead search box. Two assertions on the built dist —
// the Pagefind bundle exists, and the member index the modal actually
// fetch-loads (the emitted reference.json asset, located via the
// modal's own data-ref-url in the built HTML) parses non-empty.
// Node, not weir: this runs inside the site's npm toolchain in the
// deploy job, which has no weir binary at build time.
import { existsSync, readFileSync } from "node:fs";

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
  (ref.forms ?? []).length;
if (count === 0) fail(`member index dist${m[1]} parsed empty`);

console.log(`search guard: pagefind bundle present, member index has ${count} entries`);
