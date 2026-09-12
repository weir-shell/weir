# weir — module signatures: a signature IS the export

Status: APPROVED (2026-09-12, designer ruling). Driver: the module
boundary is the LAST untyped wire — weir refuses inference at every
other boundary (json/yaml/xml/env/args all demand declared shapes), and
the KSL-port findings showed module code hitting inference walls scripts
never see (members generalize eagerly with no annotation escape hatch).
Ruling: inference stays great in scripts and module INTERNALS; the
PUBLIC surface must be unambiguous — annotations mandatory ONLY on
exported members.

## The fusion — one construct, both features

Weir has today NO visibility mechanism (every module `let` exports,
probed: Members list built wholesale) and NO annotation syntax (a
headless `let f : int -> int` is a parse error; param annotations refuse
everywhere). Instead of adding both separately (`pub` + annotations),
ONE construct does both:

    module Kust

    /// add a resource to a kustomization document
    let addRes : string -> Yaml -> Yaml

    let addRes resPath doc =
        ...

    let joinPwd pwd p =        // unsigned — module-PRIVATE, full inference
        ...

- `let name : <arrow-type>` with NO `=` is a SIGNATURE declaration —
  and the signature IS the export. Unsigned member = private, invisible
  to importers, inference untouched. No pub keyword, no export list;
  mandatory-annotations-on-exports holds BY CONSTRUCTION (you cannot
  export without the signature because the signature is the export).
- IMPLEMENTATIONS stay annotation-free (the OCaml .mli posture, inline).
- CHECK-AGAINST-SIG, not infer-then-compare: the impl is checked WITH
  the signature's types flowing in — which is what retires the KSL
  findings (param constructor-patterns get their scrutinee type from
  the sig; a generic result refusing `to yaml` is pinned by it).
- Scripts REFUSE the form with a teaching ("signatures belong to module
  APIs — scripts infer"), keeping the script surface clean by law.

## Laws

- Sig without impl in the module = check error at the sig ("signature
  without implementation"); impl without sig = private (never an error).
- Sig/impl mismatch = located teaching naming BOTH sites and both types.
- Importing an unsigned member teaches the migration: "'M.helper' is
  module-private (no signature) — add `let helper : <ty>` in the module
  to export it" + did-you-mean over the SIGNED members.
- `///` docs attach to the SIGNATURE line (the API doc home); hover and
  #help read sig + doc. A doc on the impl of a signed member is a
  check-time "doc belongs on the signature" teaching (one home).
- `type` declarations stay AUTO-EXPORTED in v1 — a type declaration is
  already fully explicit (no inference to pin down); the asymmetry is
  stated. Private types are a receipt-driven follow-up.
- BREAKING migration, accepted: today every member is unsigned and
  public; after this, unsigned = private. Pre-1.0, small corpus (the
  repo's own modules + the known ports); the import teaching makes the
  migration self-guiding. Sweep the repo's own modules in the same
  branch.

## Probes (before/while building)

1. Generic signatures: does the arrow-type production ([D:function-types]
   F1) admit type variables (`'a -> 'a`)? If not, size admitting them —
   exported generic members need a spelling (Seq-like helpers).
2. The enforcement point: module loader (Script.fs loadModuleCached →
   Members merge) — unsigned members simply not merged; they typecheck
   internally with full inference.
3. Attribute placement for signed members ([<...>] on sig vs impl) —
   pick ONE home (lean: the signature, with the doc).
4. LSP: hover on an imported member reads the sig verbatim; definition
   jumps to the sig line (impl one hop further).
5. The assembler: a headless `let name : ty` line must not join with
   the next line (a new statement shape — fuzz owed if the assembler
   moves).
6. Effects hook, NOTED not designed: the signature line is where a
   future effect row would live ([D:pure] Stage 2+).

## What it retires from the KSL-findings family

- Param pattern-matching in module fns (sig types flow in) — RETIRED.
- Generic result refusing `to yaml` — RETIRED (sig pins it).
- Stays separate: `fail` not bottom-typed (own small fix); tuple
  destructure in lambda bodies + multi-line application in module
  bodies (assembler bugs, own branches); qualified ctors in patterns
  (docs-only).

## Footprint

Parser (headless-sig production, module files only — script refusal
teaching); Script.fs (module loader: sig collection, export filtering,
sig/impl pairing); Check (check-against-sig, mismatch/sig-orphan
teachings); Lsp (hover/definition via sigs); tests (pins for every law
above + migration teaching); e2e cell (a signed/unsigned module
end-to-end incl. the private-member refusal); repo module sweep; SKILL
(module section rewrite) + GUIDE + COMING-FROM (the F#/.mli row);
[D:module-signatures] DECISIONS row; CHANGELOG. Fuzz owed if the
assembler moves (probe 5).
