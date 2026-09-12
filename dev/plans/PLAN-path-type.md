# weir — Path as a type, not a string

Status: DRAFT (2026-09-11). Undiscussed — this file is a first design pass
and a set of open questions, not an approved shape. Bet #2 of the
"features F# lacks that fit weir" set.

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
   - Overload: fs builtins accept `Path` OR `string` (string literal
     coerces) — no migration, gradual adoption. Likely the pragmatic v1.
   - Leave fs builtins on `string`; `Path` is an OPT-IN type for path math
     you `show` back to a string at the boundary — smallest, but the type
     never reaches the fs surface (weak).
   This choice dominates the cost; it is the real design question.

3. ABSOLUTE vs RELATIVE. In the TYPE (two types / a phantom flag, so a
   relative path cannot be passed where absolute is required — strong,
   F#-units-of-measure-flavored) or at RUNTIME (`Path.isAbs`, one type —
   simple)? Rec: runtime in v1; type-level abs/rel is a deferred
   refinement with its own receipt (a bug where rel/abs confusion bit).

## Sketch (v1, explicit construction, overloaded boundary)

    let root = Path.of "src"
    let proj = root |> Path.join "App" |> Path.join "App.csproj"   // Path, pure
    proj |> Path.ext           // "csproj"
    proj |> Path.exists        // bool, fs.read effect
    File.read proj             // fs builtins accept Path (string literals still coerce)
    glob "src/**/*.csproj"     // -> seq<Path>

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
