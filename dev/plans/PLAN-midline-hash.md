# weir — mid-line `#` in districts: the two paths agree

Status: EXECUTED (2026-07-31; blessed same day). One small session.
Origin: the content-is-bytes audit recorded "mid-line `#` on district
structure lines is data while the read side strips it" as a STATED
ASYMMETRY; re-read at review it is a weir-vs-weir divergence with a
determined fix direction — YAML's own rule says the read side is
right.

Session report: all work items landed — see [D:district-hash] and
NOTES "mid-line `#` in districts". The cut is one machine with two
faces (Yaml.commentCutAt, `holes` parameter); structure lines and
`for` headers cut before dispatch; block content protected by the
existing consumed-before-scanners ordering. Corpus sweep: ZERO
occurrences — no pin movement. Bonus: trailing comments after
splices now parse (errored before). Grammars engine-verified
(tree-sitter scanner `#` stop + hash_line painting; TextMate
catch-all lookbehind for glued `a#b`; micro exempt). GRAMMAR.md's
denominator now states the fuzzer's yaml-district gap. The five-case
table lives in SEMANTICS, stated once; the audit row's "accepted
asymmetry" phrasing corrected in place. Acceptance held: the pasted
manifest emits `image: nginx:latest`, unquoted.

The defect, precisely: a district valued `nginx:latest # pinned by
ops` where `from yaml` on the same text valued `nginx:latest`; the
reverse-Norway law then faithfully quoted the polluted value into
generated YAML — silently wrong, with no error anywhere. Nothing
lost by the fix: a comment never reached the output as a comment
(districts build nodes); it used to survive attached to the wrong
thing, now it disappears, which is what a comment is for.
