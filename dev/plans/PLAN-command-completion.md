# PLAN — command-argument completion (the shell-completions story)

weir completes command HEADS (PATH, bindings, aliases) and, after a
command, only paths and `$` splices. There is no completion of a tool's
subcommands, flags or values — what bash-completion, zsh's `_git` or
fish's completions give.

The data mostly exists already: `weir add sig <tool>` builds a
per-project signature from the tool's own sources (fish completions,
cobra `__complete`, git's completion helper, `--help` tables) with
subcommands, flags and arities ([D:sig-sources], [D:sig-help-shapes]).
And the LSP already completes subcommands and flags from a sig — but
only in a script with a `#sig` line (Lsp.fs `sigFlags`/`nearestSig`,
[D:scoped-sigs]). The REPL has nothing.

## Layer 1 — signature-driven completion (static, offline)

1. **One completer, shared.** Move the LSP's sig completion into
   Complete.fs (subcommand words after the tool, flags after `-`, the
   longest-matching case, the globals rule) so the LSP and the REPL Tab
   use the same code and agree.
2. **Which sigs the REPL sees.** The REPL has no `#sig` line. Load, by
   tool name at Tab time:
   - the project's `.weir/sigs/<tool>.weir` (the nearest `.weir/` up from
     the cwd, as scripts resolve it);
   - then a user-level sig dir (`$XDG_CONFIG_HOME/weir/sigs/`), so a sig
     can serve every directory;
   - nothing found: today's path completion, unchanged.
   Loaded sigs are cached per session, keyed by path + mtime.
3. **What completes where.**
   - after the tool (and after each subcommand word): the case's
     subcommand words;
   - a word starting with `-`: the matched case's flags (longs as
     `--name`, shorts as `-x`), plus `--no-x` where the sig is
     negatable;
   - after a flag that takes a value: by arity — a path-ish value gets
     path completion, an enumerated value (when the sig records the
     choices) gets them, otherwise nothing (no false offers);
   - after `stopAtOperand` / `Unchecked` / an open tool: paths only (the
     sig can't know what comes next);
   - `--flag=` completes the value part.
4. **Discoverability.** `weir add sig <tool>` already says where it
   wrote; a one-line note in `#help`/docs that a sig also drives Tab.
   No new directive.
5. **Pins.** Unit tests over the sig fixtures in tests/sigs (gitfx,
   cobrafx, fishfx, clapfx, killfx…): subcommands, flags per case,
   values by arity, the stop/unchecked/open rules; a pty test typing
   `gitfx com<TAB>`, `gitfx commit --am<TAB>`; LSP e2e unchanged or
   extended for the moved code.

## Layer 2 — dynamic values from the tool itself (opt-in)

Static sigs can't know resource names (`kubectl get pods <TAB>`), branch
names (`git checkout <TAB>`) or PR numbers. Some tools answer at
completion time:

- **cobra tools** (kubectl, helm, gh, docker…): `tool __complete <args…>
  <partial>` prints candidates and a directive line;
- **git**: refs/remotes/paths via `git for-each-ref` etc. (the shapes
  git's own bash completion uses);
- **fish-completion-only tools**: their scripts call commands weir can't
  run without fish — out of scope.

Rules (each to confirm below):
- runs a program on Tab, so it is **opt-in per tool** (a sig-level
  `let dynamic = true` written by `add sig` when it detects cobra, or a
  user config list);
- **bounded**: a short timeout (e.g. 300 ms), killed on expiry or on the
  next key; a slow tool yields nothing, never a frozen editor;
- the child runs in the session's cwd and env, stdin empty, no tty;
- results cached for the rest of the edit (same args → same answer);
- the output is untrusted text: escaped for display ([D:error-data-escape]),
  inserted only as an argv word (quoted when it needs it).

## Decisions (user 2026-10-22)

- D1: sigs are found in the nearest project `.weir/sigs/`, then the user
  dir `$XDG_CONFIG_HOME/weir/sigs/`; `weir add sig --global <tool>`
  writes to the user dir.
- D2: layer 2 is opted into by the sig: `weir add sig` writes
  `let dynamic = true` when it detects a cobra tool or git (deletable);
  300 ms bound, killed on expiry or the next key, cached per edit.
- D3: layer 2 is REPL-only. A script names what it means, not what
  exists on the author's machine right now (a pod or branch name from
  the current cluster/repo is rarely what a script should hard-code), so
  the LSP keeps layer 1 (subcommands, flags, values by arity) only.

- D4 (user 2026-10-23, refines D2): the opt-in names its mechanism —
  `let dynamic = "git"` or `let dynamic = "cobra"`. Project sigs load
  automatically, so a planted sig must never make Tab run a program on
  the sig's say-so (`rm __complete foo` on a non-cobra tool). `"git"`
  runs only fixed read-only git commands built into weir (never sig
  text), so any sig may use it. `"cobra"` is honored only from the user
  dir (`add sig --global`); a project sig asking for it is ignored, with
  a one-line hint to install it globally.

## Order

Layer 1 first (one release), layer 2 after (its own release), each with
the usual gates (unit, e2e, REPL storms, harnesses; layer 2 adds a
storm-style check that a hung or flooding `__complete` never blocks the
editor and leaves no child behind).
