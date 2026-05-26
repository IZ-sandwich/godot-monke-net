#!/usr/bin/env python3
"""Extract rollback depth distributions per condition from a run folder.
Reads [PRED-ROLLBACK] resimTicks=N for in-cap rollbacks AND computes the
implicit "would-have-been" depth for snap-on-overflow events (= clientTick at
time of snap - snapshot tick). The latter shows depths that the cap actually
prevented.

Usage: python rollback_depth_distribution.py <run_folder> [label]
"""
import os, re, sys, statistics

ROLLBACK_RX = re.compile(r"\[PRED-ROLLBACK\] tick=(\d+) entities=\d+ resimTicks=(\d+)")
SNAP_OVF_RX = re.compile(r"\[PRED-SNAP-OVERFLOW\] tick=(\d+)")
TICK_RX     = re.compile(r"\[CLIENT-TICK\] tick=(\d+)")


def quantile(values, q):
    if not values: return 0
    s = sorted(values)
    idx = min(int(len(s) * q), len(s) - 1)
    return s[idx]


def percentiles(values):
    if not values: return (0, 0, 0, 0, 0)
    s = sorted(values)
    n = len(s)
    return (s[0], s[n//2], s[int(n*0.95)], s[int(n*0.99) if n>20 else -1], s[-1])


def parse(log):
    """Returns (resim_depths_list, snap_overflow_depths_list)."""
    resim_depths = []
    snap_depths = []
    # We need to know the most-recent client tick to compute snap-overflow depth.
    last_client_tick = 0
    with open(log, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = TICK_RX.search(line)
            if m:
                last_client_tick = int(m.group(1))
                continue
            m = ROLLBACK_RX.search(line)
            if m:
                resim_depths.append(int(m.group(2)))
                continue
            m = SNAP_OVF_RX.search(line)
            if m:
                snap_tick = int(m.group(1))
                # Depth = how stale the snapshot was relative to where the
                # client clock had advanced to. last_client_tick is updated
                # every physics frame so this is a tight upper bound.
                depth = max(0, last_client_tick - snap_tick)
                snap_depths.append(depth)
                continue
    return resim_depths, snap_depths


def main():
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    run = sys.argv[1]
    label = sys.argv[2] if len(sys.argv) > 2 else os.path.basename(run.rstrip("/\\"))
    print(f"\n=== {label} — rollback depth distributions ===")
    print(f"{'cond':>4}  {'n_resim':>7} {'r_min':>5} {'r_P50':>5} {'r_P95':>5} {'r_P99':>5} {'r_max':>5}  |  "
          f"{'n_snap':>6} {'s_min':>5} {'s_P50':>5} {'s_P95':>5} {'s_P99':>5} {'s_max':>5}")
    for cond in ["C0", "C1", "C2", "C3", "C4"]:
        log = os.path.join(run, f"S7-MultiBodyChaos.{cond}.client.log")
        if not os.path.exists(log):
            print(f"{cond}: log missing"); continue
        resim, snap = parse(log)
        r = percentiles(resim); s = percentiles(snap)
        print(f"{cond:>4}  {len(resim):>7} {r[0]:>5} {r[1]:>5} {r[2]:>5} {r[3]:>5} {r[4]:>5}  |  "
              f"{len(snap):>6} {s[0]:>5} {s[1]:>5} {s[2]:>5} {s[3]:>5} {s[4]:>5}")


if __name__ == "__main__":
    main()
