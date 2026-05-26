#!/usr/bin/env python3
"""Detect physics-tick freezes by tracking the wallclock-vs-physics-frame
relationship across SMOOTH-FRAME log entries. The MonkeLogger timestamp
seconds field intermittently wraps wrong, so use a monotonic-fixup parser
that adds 60s whenever timestamps go backward.

Outputs (event, pf, gap_ms) where a freeze is any inter-frame gap > threshold.
"""
import os, re, sys, glob
from datetime import datetime

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Match SMOOTH-FRAME body=1 entries — pf is the engine's physics frame counter,
# which only advances when a physics tick fires. Wallclock gap between
# consecutive samples that map to ONE pf advance = freeze duration.
FRAME_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[SMOOTH-FRAME\] body=1 pf=(\d+)"
)


def find_log():
    pat = os.path.join(REPO, "tests", "TestResults", "Quantitative", "*", "S7-MultiBodyChaos.C4.client.log")
    matches = sorted(glob.glob(pat))
    return matches[-1] if matches else None


def parse_monotone(ts_str, last_dt):
    """Parse a MonkeLogger timestamp into seconds-since-epoch, adjusting for
    the seconds-field wrap bug (we add minutes/60s offsets as needed to
    maintain monotonicity vs the previous value).
    """
    dt = datetime.strptime(ts_str, "%Y-%m-%dT%H:%M:%S.%f")
    if last_dt is not None and dt < last_dt:
        # MonkeLogger wrap: add minutes until monotone
        from datetime import timedelta
        while dt < last_dt:
            dt = dt + timedelta(seconds=60)
    return dt


def main():
    log = sys.argv[1] if len(sys.argv) > 1 else find_log()
    threshold_ms = float(sys.argv[2]) if len(sys.argv) > 2 else 30.0
    print(f"reading {log}, threshold {threshold_ms} ms")

    samples = []
    last_dt = None
    with open(log, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = FRAME_RX.search(line)
            if not m: continue
            dt = parse_monotone(m.group(1), last_dt)
            pf = int(m.group(2))
            samples.append((dt, pf))
            last_dt = dt

    if len(samples) < 2:
        print("no samples"); return

    # Find render-frame gaps where wallclock jumps but pf advances by 1
    # (engine processed exactly one physics tick but it took a long time =
    # that tick was heavy = freeze).
    freezes = []
    for i in range(1, len(samples)):
        dt_prev, pf_prev = samples[i-1]
        dt_now, pf_now = samples[i]
        gap_ms = (dt_now - dt_prev).total_seconds() * 1000.0
        pf_advance = pf_now - pf_prev
        if gap_ms > threshold_ms:
            freezes.append((pf_prev, pf_now, gap_ms, pf_advance))

    freezes.sort(key=lambda x: -x[2])
    print(f"saw {len(samples)} smooth-frame samples; gaps > {threshold_ms} ms: {len(freezes)}")
    print("\nTop 25 freezes (sorted by wallclock duration):")
    print(f"  {'pf_from':>8} {'pf_to':>8} {'gap_ms':>10} {'pf_advance':>11}")
    for pf_prev, pf_now, ms, adv in freezes[:25]:
        print(f"  {pf_prev:8d} {pf_now:8d} {ms:10.1f} {adv:11d}")


if __name__ == "__main__":
    main()
