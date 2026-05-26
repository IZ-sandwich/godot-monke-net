#!/usr/bin/env python3
"""Quantify whether running the SpaceStep resim actually pulls the body
closer to server-truth, vs just snapping to the auth pose.

Methodology
-----------
For every PRED-CHECK event for an entity we capture:
  - tick               (snapshot tick)
  - predPos            (client's predicted pose at that tick)
  - authPos            (server's authoritative pose at that tick)
  - posDiff            (|authPos - predPos| — the pre-reconcile error)
For every PRED-ROLLBACK we capture:
  - tick               (snapshot tick the reconcile is for)
  - resimTicks         (depth of the resim loop = ticks of physics replayed)

Key insight: if resim works, then on the NEXT snapshot to arrive for an entity
the pre-reconcile error should be smaller than what it would have been without
the resim. We approximate "without resim" by comparing pairs:
  - snapshots that triggered a resim → next snapshot's |posDiff|
  - snapshots that triggered snap-overflow → next snapshot's |posDiff|
A SECOND run of the same scenario with `snap-always` enabled (no resim ever)
is the cleaner A/B and is what `--ab` mode below compares.

Usage:
  Single-run mode:
      python resim_effectiveness.py <run_folder>

  A/B mode (compare resim run vs snap-always run):
      python resim_effectiveness.py --ab <resim_run> <snap_always_run>
"""
import os, re, sys, statistics

PRED_CHECK_RX = re.compile(
    r"\[PRED-CHECK\] tick=(\d+) eid=(\d+) "
    r"predPos=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"authPos=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"\|posDiff\|=([\d.]+)m "
    r"predVel=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)"
)
ROLLBACK_RX = re.compile(r"\[PRED-ROLLBACK\] tick=(\d+) entities=\d+ resimTicks=(\d+)")
SNAP_OVF_RX = re.compile(r"\[PRED-SNAP-OVERFLOW\] tick=(\d+)")
TICK_RX     = re.compile(r"\[CLIENT-TICK\] tick=(\d+) dt=")


def parse_log(log_path):
    """Returns list of events in time-order: each event is a dict with type/tick/fields."""
    events = []
    cur_client_tick = 0
    with open(log_path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = TICK_RX.search(line)
            if m:
                cur_client_tick = int(m.group(1))
                continue
            m = PRED_CHECK_RX.search(line)
            if m:
                events.append({
                    "type": "check",
                    "tick": int(m.group(1)),
                    "eid": int(m.group(2)),
                    "pred_pos": (float(m.group(3)), float(m.group(4)), float(m.group(5))),
                    "auth_pos": (float(m.group(6)), float(m.group(7)), float(m.group(8))),
                    "diff": float(m.group(9)),
                    "client_tick": cur_client_tick,
                })
                continue
            m = ROLLBACK_RX.search(line)
            if m:
                events.append({"type": "rollback", "tick": int(m.group(1)), "depth": int(m.group(2)), "client_tick": cur_client_tick})
                continue
            m = SNAP_OVF_RX.search(line)
            if m:
                events.append({"type": "snap_ovf", "tick": int(m.group(1)), "client_tick": cur_client_tick})
    return events


def quantiles(values):
    if not values: return (0, 0, 0, 0, 0)
    s = sorted(values)
    n = len(s)
    return (s[0], s[n // 4], s[n // 2], s[int(n * 0.95)], s[-1])


def fmt_q(values, fmt="{:.4f}"):
    if not values: return "no data"
    q = quantiles(values)
    return f"min={fmt.format(q[0])} P25={fmt.format(q[1])} P50={fmt.format(q[2])} P95={fmt.format(q[3])} max={fmt.format(q[4])}"


def analyse_single(run_folder, cond, eid_filter=None):
    """Examine how pre-reconcile error evolves around resim events."""
    log = os.path.join(run_folder, f"S7-MultiBodyChaos.{cond}.client.log")
    if not os.path.exists(log): return None
    events = parse_log(log)
    # Group consecutive PRED-CHECK + (maybe rollback)
    # For each entity, walk in time order.
    by_entity = {}
    for e in events:
        if e["type"] == "check":
            by_entity.setdefault(e["eid"], []).append(e)
    # Per-rollback: track the "depth" the next check sees.
    rollback_depths = [e["depth"] for e in events if e["type"] == "rollback"]
    snap_ovf_count = sum(1 for e in events if e["type"] == "snap_ovf")
    checks_per_entity = {eid: len(lst) for eid, lst in by_entity.items()}
    return {
        "cond": cond,
        "n_checks": sum(checks_per_entity.values()),
        "n_rollbacks": len(rollback_depths),
        "n_snap_ovf": snap_ovf_count,
        "rollback_depth_q": quantiles(rollback_depths) if rollback_depths else None,
        "by_entity": by_entity,
        "events": events,
    }


def cmp_pre_reconcile_errors(resim_data, snap_data, eid_filter=None, tick_window=None):
    """Compare pre-reconcile |posDiff| distributions between resim & snap modes."""
    def collect(data):
        out = []
        for eid, lst in data["by_entity"].items():
            if eid_filter is not None and eid != eid_filter: continue
            for e in lst:
                if tick_window and not (tick_window[0] <= e["tick"] <= tick_window[1]): continue
                out.append(e["diff"])
        return out

    rs = collect(resim_data)
    ss = collect(snap_data)
    return rs, ss


def main_ab(resim_run, snap_run):
    print(f"\n=== A/B comparison: resim vs snap-always (cap=25) ===")
    print(f"  resim run:     {resim_run}")
    print(f"  snap-always:   {snap_run}")

    for cond in ["C0", "C1", "C2", "C3", "C4"]:
        rd = analyse_single(resim_run, cond)
        sd = analyse_single(snap_run, cond)
        if rd is None or sd is None: continue
        print(f"\n--- {cond} ---")
        print(f"  resim:        checks={rd['n_checks']:>5}  rollbacks={rd['n_rollbacks']:>4}  snap_ovf={rd['n_snap_ovf']:>4}  rb_depth={rd['rollback_depth_q']}")
        print(f"  snap-always:  checks={sd['n_checks']:>5}  rollbacks={sd['n_rollbacks']:>4}  snap_ovf={sd['n_snap_ovf']:>4}")

        # Per-entity-type buckets
        # eid=1 = player; eids 2..21 = cubes/balls (spawned in S7)
        for label, predicate in [
            ("ALL entities",     lambda e: True),
            ("player (eid=1)",   lambda e: e == 1),
            ("non-player",       lambda e: e != 1),
        ]:
            rs_diffs = []
            ss_diffs = []
            for eid, lst in rd["by_entity"].items():
                if not predicate(eid): continue
                rs_diffs.extend(c["diff"] for c in lst)
            for eid, lst in sd["by_entity"].items():
                if not predicate(eid): continue
                ss_diffs.extend(c["diff"] for c in lst)
            if not rs_diffs and not ss_diffs: continue
            print(f"    {label}:")
            print(f"      resim       (n={len(rs_diffs):>5}): {fmt_q(rs_diffs)}")
            print(f"      snap-always (n={len(ss_diffs):>5}): {fmt_q(ss_diffs)}")

        # Per-phase: spawn-roll (early ticks) vs player-collision (later ticks)
        # Look at the actual tick range in the data:
        all_ticks = [c["tick"] for lst in rd["by_entity"].values() for c in lst]
        if not all_ticks: continue
        tmin, tmax = min(all_ticks), max(all_ticks)
        split = tmin + (tmax - tmin) // 3  # roughly first-third = spawn-roll
        print(f"    Phase split: tick range [{tmin}..{tmax}], spawn-roll=[{tmin}..{split}], collision=[{split+1}..{tmax}]")

        for phase, window in [("spawn-roll (first ~third)", (tmin, split)),
                              ("collision (rest)",          (split + 1, tmax))]:
            rs_p = [c["diff"] for lst in rd["by_entity"].values() for c in lst if window[0] <= c["tick"] <= window[1]]
            ss_p = [c["diff"] for lst in sd["by_entity"].values() for c in lst if window[0] <= c["tick"] <= window[1]]
            print(f"    {phase}:")
            print(f"      resim       (n={len(rs_p):>5}): {fmt_q(rs_p)}")
            print(f"      snap-always (n={len(ss_p):>5}): {fmt_q(ss_p)}")

        # Resim depth correlation: pair each rollback with the next PRED-CHECK
        # for player. Stratify by rollback depth.
        # Build time-ordered list of player checks vs rollbacks.
        # Walk events: when a rollback fires, find the NEXT player check after it.
        depth_buckets = {(0, 5): [], (6, 10): [], (11, 25): []}
        ev = rd["events"]
        for i, e in enumerate(ev):
            if e["type"] != "rollback": continue
            d = e["depth"]
            bucket = next((b for b in depth_buckets if b[0] <= d <= b[1]), None)
            if bucket is None: continue
            # Find next player check
            for j in range(i + 1, len(ev)):
                if ev[j]["type"] == "check" and ev[j]["eid"] == 1:
                    depth_buckets[bucket].append(ev[j]["diff"])
                    break
        print(f"    [resim only] player |diff| AFTER rollback, stratified by depth:")
        for bucket, diffs in depth_buckets.items():
            if not diffs: continue
            print(f"      depth {bucket[0]:>2}-{bucket[1]:>2} (n={len(diffs):>4}): {fmt_q(diffs)}")


def main_single(run_folder):
    print(f"\n=== single-run summary: {run_folder} ===")
    for cond in ["C0", "C1", "C2", "C3", "C4"]:
        d = analyse_single(run_folder, cond)
        if d is None: continue
        all_diffs = [c["diff"] for lst in d["by_entity"].values() for c in lst]
        print(f"  {cond}: checks={d['n_checks']:>5} rollbacks={d['n_rollbacks']:>4} snap_ovf={d['n_snap_ovf']:>4} |diff| {fmt_q(all_diffs)}")
        if d["rollback_depth_q"]:
            print(f"       rollback depth: {d['rollback_depth_q']}")


def main():
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    if sys.argv[1] == "--ab":
        if len(sys.argv) < 4: print(__doc__); sys.exit(1)
        main_ab(sys.argv[2], sys.argv[3])
    else:
        main_single(sys.argv[1])


if __name__ == "__main__":
    main()
