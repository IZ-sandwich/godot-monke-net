#!/usr/bin/env python3
"""Run smoother_divergence.py on every S7 condition log under a run folder
and print a one-line summary per condition. Used during the resim-vs-snap
hypothesis exploration to compare runs at a glance.

Usage: python hypothesis_runner.py <run_folder>
"""
import os, re, sys, subprocess

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def parse_summary(stdout: str):
    # Extract from the smoother_divergence.py output the few numbers we care about
    out = {}
    m = re.search(r"parsed (\d+) render frames, (\d+) fixups, .* (\d+) snap-rx, .* (\d+) snap-overflow, .* (\d+) reconciles, (\d+) rollbacks", stdout)
    if m:
        out["render"] = int(m.group(1)); out["snap_rx"] = int(m.group(3)); out["snap_overflow"] = int(m.group(4)); out["reconciles"] = int(m.group(5))
    m = re.search(r"P50=([\d.]+)\s+P95=([\d.]+)\s+P99=([\d.]+)\s+MAX=([\d.]+)", stdout)
    if m:
        out["off_p50"] = float(m.group(1)); out["off_p95"] = float(m.group(2)); out["off_p99"] = float(m.group(3)); out["off_max"] = float(m.group(4))
    m = re.search(r"P50=([\d.]+)\s+P95=([\d.]+)\s+P99=([\d.]+)\s+MAX=([\d.]+).*\n  freezes >50ms: (\d+)", stdout)
    if m:
        out["dt_p50_ms"] = float(m.group(1)); out["dt_p95_ms"] = float(m.group(2)); out["dt_max_ms"] = float(m.group(4)); out["freezes_50"] = int(m.group(5))
    m = re.search(r"freezes >100ms: (\d+)", stdout)
    if m: out["freezes_100"] = int(m.group(1))
    return out


def main():
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    run = sys.argv[1]
    label = sys.argv[2] if len(sys.argv) > 2 else os.path.basename(run.rstrip("/\\"))
    rows = []
    for cond in ["C0", "C1", "C2", "C3", "C4"]:
        log = os.path.join(run, f"S7-MultiBodyChaos.{cond}.client.log")
        if not os.path.exists(log):
            print(f"{cond}: log missing")
            continue
        out_dir = os.path.join(run, cond)
        r = subprocess.run(
            ["python", os.path.join(REPO, "tools", "smoother_divergence.py"), log, out_dir],
            capture_output=True, text=True,
        )
        s = parse_summary(r.stdout)
        s["cond"] = cond
        rows.append(s)

    # Summary CSV row from the run's summary.csv if present
    s_csv = os.path.join(run, "summary.csv")
    m5 = {}
    if os.path.exists(s_csv):
        with open(s_csv, encoding="utf-8") as f:
            for line in f:
                if line.startswith("S7-MultiBodyChaos,"):
                    parts = line.strip().split(",")
                    cond, m5_rms, m5_p95 = parts[1], parts[11], parts[12]
                    m5[cond] = (float(m5_rms), float(m5_p95))

    print(f"\n=== {label} ===")
    print(f"{'cond':>4} {'frzs>100':>9} {'frzs>50':>8} {'dt_p50':>7} {'dt_max':>7} {'reconc':>7} {'snap_ov':>8} {'off_max':>8} {'off_p99':>8} {'M5_RMS':>8} {'M5_P95':>8}")
    for r in rows:
        m5r, m5p = m5.get(r["cond"], (0, 0))
        print(f"{r['cond']:>4} {r.get('freezes_100',0):>9} {r.get('freezes_50',0):>8} {r.get('dt_p50_ms',0):>7.1f} {r.get('dt_max_ms',0):>7.1f} {r.get('reconciles',0):>7} {r.get('snap_overflow',0):>8} {r.get('off_max',0):>8.3f} {r.get('off_p99',0):>8.3f} {m5r:>8.3f} {m5p:>8.3f}")


if __name__ == "__main__":
    main()
