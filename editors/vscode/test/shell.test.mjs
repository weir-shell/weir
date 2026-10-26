import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { quoteForShell } from "../out/shell.mjs";

const hostile = [
  "$(touch pwned).weir",
  "`id`.weir",
  "$HOME/x.weir",
  "a b.weir",
  "it's.weir",
  "back\\slash.weir",
  "new\nline.weir",
  '"quoted".weir',
];

test("posix: the word comes back from a real shell byte for byte", { skip: process.platform === "win32" }, () => {
  for (const w of hostile) {
    const q = quoteForShell(w, "linux");
    const out = execFileSync("sh", ["-c", `printf %s ${q}`]).toString();
    assert.equal(out, w);
  }
  assert.equal(quoteForShell("it's.weir", "linux"), "'it'\\''s.weir'");
});

test("windows: a plain path quotes, a shell-active one is refused", () => {
  assert.equal(quoteForShell("C:\\Users\\me\\a b.weir", "win32"), '"C:\\Users\\me\\a b.weir"');
  for (const w of ["$(x).weir", "`x`.weir", "%TEMP%.weir", "a!b.weir", "a&b.weir", 'a"b.weir', "new\nline.weir"]) {
    assert.equal(quoteForShell(w, "win32"), null, w);
  }
});
