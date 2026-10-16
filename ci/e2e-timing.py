#!/usr/bin/env python3
# e2e cost report: stdin is ci/e2e.sh's output; each line is echoed with the
# elapsed time since the previous one (the time that check took), and the
# slowest checks are listed at the end. Python, not weir: the timer must not
# depend on the binary under test (dev/NOTES-agent.md fallbacks).
import sys
import time

TOP = 30
t0 = last = time.monotonic()
costs = []
for line in sys.stdin:
    now = time.monotonic()
    dt = now - last
    last = now
    sys.stdout.write(f"[{now - t0:7.1f}s +{dt:6.2f}s] {line}")
    sys.stdout.flush()
    costs.append((dt, line.rstrip("\n")))

total = time.monotonic() - t0
print(f"\ne2e timing: {len(costs)} lines in {total:.0f}s; the {TOP} slowest:")
for dt, line in sorted(costs, reverse=True)[:TOP]:
    print(f"  {dt:7.2f}s ({100 * dt / max(total, 1e-9):4.1f}%)  {line[:140]}")
