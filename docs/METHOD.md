# The method

weir was written almost entirely by an AI agent, directed by one
person. This page is about the part of that worth reading: not what
the agent produced, but **how the process failed, what caught the
failures, and what still needed a human**. Everything here was
recorded as it happened, in a decision ledger the repository carries —
which makes weir an unusually well-instrumented answer to a question
people mostly argue about from intuition: how far does agentic
development go, and how much human judgement does it need?

The short answer from this project: further than expected on volume
and rigor, exactly as far as the instruments around it — and the
instruments are where the real work went.

## The instruments failed before the subjects did

Seven times during weir's adversarial reviews, a *measuring device*
failed before the thing it measured did. A control test borrowed its
payload from the finding it was supposed to control for. A message
census graded a preamble instead of the messages. A canary assertion
used surface that did not exist in the window it guarded. A ledger
check counted itself and then died of it. A probe reported success
from a process that had been born unable to hear. A row asserting
Windows-path behaviour passed vacuously, because the path string was
a parse error before the assertion could run.

**Every one of those failures was toward a pass.** That is the
finding: when an instrument breaks, it almost never breaks toward the
alarm. A green light is compatible with a working system, a broken
test, an empty input, and a check that never ran — and nothing in the
color tells you which.

The rule that came out of it: **every zero needs a matching non-zero
from the same instrument.** A gate that reports "no defects" is
untrusted until it has been watched reporting a defect — a
deliberately planted one if necessary. weir's release gates were each
tested in their failing direction before anything relied on them, and
the ones that could not be (a release-publish coupling that only
fails during a real release) carry that fact in writing instead of an
implied pass.

## Parked, with a trigger

Many decisions in the ledger are refusals: a feature declined, an
option not taken. The discipline that made refusals cheap was
attaching a **reopening condition** to each one — not "no", but "not
until X". Parked items carry the event that would justify revisiting
them.

The reason this matters: the triggers *fired*. More than once, on
schedule, a parked decision's stated condition arrived and the item
reopened with its original reasoning intact — no re-litigation, no
archaeology, no "why didn't we do this before". A refusal with a
trigger is a decision; a refusal without one is a mood.

## Priced wrong, caught by probing

Twice, a rule was inherited by a new feature because it governed a
similar-looking older one — and the inherited rule's justification
did not actually transfer. Refutable record patterns were parked as
expensive when the rule that made them cheap already existed in the
language; a known-type requirement was applied to record patterns
because constructor patterns had it, though the two rest on different
needs entirely.

Both were caught the same way: by **probing the compiler rather than
reasoning about it**. The agent's reasoning inherited the mistake
fluently — reasoning is exactly the faculty that generalizes a rule
past its justification. Running twelve small programs against the
binary does not generalize. When a claim about the system could be
tested for the cost of writing a snippet, testing beat thinking every
time it disagreed with it.

## The shapes that recur

Three defect shapes appeared often enough to get names:

- **Membership lists drift.** Any hand-maintained list of "all the
  X" — keywords, members, platforms, checks — diverges from reality
  unless something enumerates reality and compares. The fix is always
  the same: derive the list, or gate the copy.
- **One rule, two enforcement points.** The same law implemented in
  two places eventually holds two opinions. Every instance was
  resolved by making one point authoritative and the other read from
  it.
- **A stated law that overreaches the code.** Documentation asserting
  more than the implementation guarantees — honest at writing time,
  false after the next change. The countermeasure is executable
  documentation: weir's docs are full of examples that *run in CI*,
  and blocks demonstrating errors that must *fail* in CI, so prose
  and binary cannot quietly part ways.

## What still needed a human

The honest limit, from the ledger's own record:

- **The rulings.** Every genuine design decision — what the language
  should refuse, which trade to take, when a behaviour is a bug
  versus a law — was made by a person. The agent proposed, probed,
  and priced; it did not decide.
- **The priorities.** What to build next, what to park, when to ship.
- **The taste calls.** "This reads badly", "this message blames the
  user", "this homepage is a wall of text" — judgements the agent
  applied once given, and did not originate.
- **The refusals to proceed.** The agent stopped and asked when an
  action was irreversible, public, or outside what had been blessed —
  and several of this project's better decisions began as exactly
  that pause.

None of this diminishes the volume: the parser, the checker, the
evaluator, the tooling, the tests, the gates, the docs and the site
were overwhelmingly agent-written. It locates the boundary: the agent
built the system; the human built the judgement the system was built
inside.

## Why this was affordable at all

A language like weir was always *possible* for one person. It was
never *affordable*: three or four years of evenings against a
hobby's expected value is a trade nobody sensible makes. Two months
of part-time direction is a project you can just do. The interesting
consequence is general — there is a class of things worth building
that nobody builds because the time cost never clears, and that class
just got smaller.

The design risk was compressed the same way the labor was: weir did
not invent a language. It selected from F#, which works, and added
the shell-specific parts. Most of what makes a language trustworthy —
its semantics having been thought through — was borrowed from a
system that had twenty years of thinking in it.

---

The receipts for all of the above live in the repository:
[docs/DECISIONS.md](DECISIONS.md) is the full ledger,
[CONTRIBUTING.md](../CONTRIBUTING.md) describes the working
arrangement, and [SECURITY.md](../SECURITY.md) states what is and is
not claimed. To check any of it: `git clone`, then `./ci/local.sh`
runs the whole battery.
