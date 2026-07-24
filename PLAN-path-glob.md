# weir — mini-plan: `Path.glob`

Status: BLESSED (user 2026-07-24). One session. GATED on scriptPath
(satisfied).

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

- AOT PROBE VERDICT: the fallback, doubly forced — FileSystemGlobbing
  is unrestorable in the offline container AND fights the SPEC before
  AOT is asked (its `*` matches dotfiles; only final match sets
  surface, so the dotfile law cannot be imposed). Hand-rolled matcher
  over the standard subset (~90 lines), whose per-segment structure
  is what the dotfile law needs anyway.
- PREMISE CORRECTED (probe-caught): the symlink bracket described
  pre-4.3 bash — modern globstar does NOT traverse symlinked dirs.
  The visited-set draft failed its own loop fixture (path identity
  through links never canonicalizes); the bash-parity law is
  loop-immune BY CONSTRUCTION: `**` skips symlinked dirs, explicit
  segments follow them. Both halves pinned.
- Decided semantics all landed and pinned in e2e: dotfile law both
  ways; sorted; lazy-at-enumeration with the cd seam taught
  (`|> Seq.force` pins — the e2e shows both sides); empty seq +
  the match-[] idiom; permission-skip (unreadable dir fixture);
  absolute/relative echo; no brace expansion (parked as bash-ism).
- Products: scriptPath composition (script-relative glob after
  `cd /` — the gate's payoff); glob |> feed (discovery into stdin);
  the splat park's SECOND receipt filed without opening (argv
  building spells Seq.append today). DEFERRED honestly: the
  distinct-dedupe cell waits for Seq.distinct (the miner session) —
  the plan listed the product ahead of its dependency.
- The no-globs LAW amended, not retired: SEMANTICS already said
  "expansion" precisely; SKILL/GUIDE now carry the function pointer;
  bare `*.txt` in argv stays a literal word. (No divergences.md row
  exists for it — bash priors are invisible to the F# oracle, per
  the bang-sigil precedent; the law lines ARE the record.)
- Timing: 10k files in 14ms against the 2s ceiling, pinned in e2e.
