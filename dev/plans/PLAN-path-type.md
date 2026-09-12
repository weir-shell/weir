# weir — Path as a type, not a string

Status: DRAFT (2026-09-11). Undiscussed — this file is a first design pass
and a set of open questions, not an approved shape. Bet #2 of the
"features F# lacks that fit weir" set.

Review ruling (2026-09-11 rider): keep DRAFT behind a REAL
rel/abs-confusion bug receipt — §3's own recommendation applied to the
whole feature. The current receipt is one KSL path helper
(`RelativeToRoot`) and `$"{a}/{b}"` is what people write; with the
overload option struck (below), the surviving v1 shapes cost more than
the feature's evidence justifies. Lowest-ranked of the current bets.

## The idea

The shell's central noun is the path, and weir models it today as `string`
with a `Path` MODULE. Make it a TYPE, the same move already made for
`Size`/`Secret`/`Duration`: take the domain's core value and give it a type
with laws. F# leaves paths as strings because file wrangling is incidental
to F#; to weir it is the main event.

## What the type buys

- Typed join that cannot produce string-concat nonsense (`Path.join`, not
  `a + "/" + b`); `Path.parent`, `.name`, `.stem`, `.ext`, `.rel`, `.abs`.
- `glob` yields `seq<Path>` instead of `seq<string>`.
- Composes with [PLAN-pure]: pure path MATH (join/parent/name) is `(pure)`;
  the operations that touch disk (`exists`, `isDir`) are `fs.read` effects —
  a clean line the effect analysis can already draw.
- Fewer "is this absolute or relative / does it end in a slash" bugs — the
  exact papercut KSL's `fullPath`/`getCwdFullPath`/`RelativeToRoot`
  helpers exist to paper over.

## Honest sizing — the TYPE is easy, the ERGONOMICS is where it hides

The nominal type + module is small (a wrapper over string + functions).
The complexity is in three decisions, each with a real cost:

1. CONSTRUCTION / LITERALS. How does a path VALUE come to be?
   - `Path.of "src/App"` (explicit) — simplest, zero grammar, but verbose
     at every call site.
   - A path literal / implicit `string -> Path` coercion in path position
     (like how scalar literals coerce) — ergonomic, but needs a coercion
     rule and a decision about WHERE it fires.
   Rec to weigh: start explicit (`Path.of`), add coercion only if the call
   sites prove it painful.

2. THE BOUNDARY MIGRATION. Do `File.read`/`File.write`/`Dir.*`/`glob` take
   `Path` or `string`? weir uses string paths EVERYWHERE today
   (`File.read "x"`). Options:
   - Migrate all fs builtins to `Path` (typed end-to-end, but a breaking
     change across every script + e2e cell + doc block).
   - STRUCK: "overload — fs builtins accept `Path` OR `string`". weir
     refuses overloading and states "no implicit widening" (coming-from's
     F# section); the option contradicts two stated laws and is not
     available as written. The adjacent LEGAL mechanism is a coercion at a
     MARKED boundary — the scalar-literal precedent (`1KiB`, `30s`): a
     string LITERAL in an fs-builtin argument position could coerce to
     `Path` if that position is marked. But that is Q1's construction
     question, and it is a real coercion rule with a "where does it fire"
     cost — not the free lunch the overload looked like.
   - Leave fs builtins on `string`; `Path` is an OPT-IN type for path math
     you render back to a string at the boundary — smallest, but the type
     never reaches the fs surface (weak).
   With the overload struck, the honest v1 choices are migrate-all (a
   repo-wide breaking sweep) or opt-in-beside-string (weak). Both are
   worse than the struck option looked — which LOWERS this feature's
   ranking, and that is the correct reading (see the status ruling).
   This choice dominates the cost; it is the real design question.

3. ABSOLUTE vs RELATIVE. In the TYPE (two types / a phantom flag, so a
   relative path cannot be passed where absolute is required — strong,
   F#-units-of-measure-flavored) or at RUNTIME (`Path.isAbs`, one type —
   simple)? Rec: runtime in v1; type-level abs/rel is a deferred
   refinement with its own receipt (a bug where rel/abs confusion bit).

## Sketch (v1, explicit construction, opt-in beside string builtins)

    let root = Path.of "src"
    let proj = root |> Path.join "App" |> Path.join "App.csproj"   // Path, pure
    proj |> Path.ext           // "csproj"
    proj |> Path.exists        // bool, fs.read effect
    File.read (Path.str proj)  // back to string at the fs boundary (opt-in v1;
                               // a typed fs surface needs Q2's migrate-all or
                               // Q1's marked literal-coercion — no overload)
    glob "src/**/*.csproj"     // -> seq<Path>, if glob migrates

## Open questions (all undiscussed — for the maintainer)

1. Construction: explicit `Path.of` only, or a coercion/literal? (Lean:
   explicit first.)
2. Boundary: migrate fs builtins to `Path`, overload Path|string, or keep
   `Path` opt-in beside string builtins? (This is THE decision.)
3. abs/rel: runtime (`Path.isAbs`) or type-level? (Lean: runtime v1.)
4. Show/serialize: `Path` shows as its string; does it cross `to json` as a
   string (yes, surely) — confirm no new wire question.
5. Is a `Url` type its sibling (same "typed core noun" argument for
   `Http`)? Note it, do not scope it here.

## Relation to other plans

Independent; composes with [PLAN-pure] (pure path math vs `fs.read` probes)
and helps [PLAN-ksl-migration] (`DirWithContext` / `RelativeToRoot` path
math). Lowest identity-risk of the four bets, but the boundary-migration
question (2) is what decides whether it is "not super complex" or a repo
-wide sweep.
