# Upsert weir's entry in zed-industries/extensions' extensions.toml —
# run from the fork checkout by ext-zed.yml. Entries are alphabetical;
# a new entry inserts in order, an existing one is rewritten in place.
import os
import re
import sys

version = os.environ["VERSION"]
entry = (
    "[weir]\n"
    'submodule = "extensions/weir"\n'
    'path = "editors/zed"\n'
    f'version = "{version}"\n'
)

with open("extensions.toml") as f:
    t = f.read()

if "\n[weir]\n" in t or t.startswith("[weir]\n"):
    t, n = re.subn(r"\[weir\]\n(?:[a-zA-Z_]+ = [^\n]*\n)+", entry, t)
    if n != 1:
        sys.exit("expected exactly one [weir] entry to rewrite, found %d" % n)
else:
    blocks = t.split("\n\n")
    out, inserted = [], False
    for b in blocks:
        bid = b.split("]", 1)[0].lstrip("[") if b.startswith("[") else ""
        if not inserted and bid and bid > "weir":
            out.append(entry.rstrip())
            inserted = True
        out.append(b)
    if not inserted:
        out.append(entry.rstrip() + "\n")
    t = "\n\n".join(out)

with open("extensions.toml", "w") as f:
    f.write(t)
print("extensions.toml: weir ->", version)
