#!/usr/bin/env python3
"""Side-by-side compare two quantitative-suite summary.csv files."""
import csv, os, sys

def load(path):
    rows = {}
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip(): continue
            if line.startswith("scenario,"): header = line.strip().split(","); continue
            cols = line.strip().split(",")
            key = (cols[0], cols[1])
            rows[key] = dict(zip(header, cols))
    return rows, header

def fmt(v):
    try:
        f = float(v)
        if abs(f) < 0.001 or abs(f) > 999: return f"{f:.2e}"
        return f"{f:.3f}"
    except Exception:
        return str(v)

def main():
    old_path, new_path = sys.argv[1], sys.argv[2]
    old, header = load(old_path)
    new, _ = load(new_path)
    # Columns of interest
    cols = ["M3_mispredict_pct", "M3b_ext_force_pct", "M4_rb_p95", "M5_pos_rms_m", "M5_pos_p95_m", "M6_visual_ratio"]
    # Print table
    keys = sorted(set(old.keys()) | set(new.keys()))
    w = 26
    print(f"{'scenario / cond':<{w}} {'metric':<22} {'old':>10} {'new':>10} {'delta':>10} {'%':>8}")
    for key in keys:
        sc, co = key
        o = old.get(key); n = new.get(key)
        label = f"{sc} / {co}"
        for c in cols:
            ov = o.get(c, "?") if o else "—"
            nv = n.get(c, "?") if n else "—"
            try:
                fo, fn = float(ov), float(nv)
                if fo == -1 or fn == -1:
                    # sentinel value, skip
                    continue
                delta = fn - fo
                pct = (delta / fo * 100) if abs(fo) > 1e-9 else None
                pct_str = f"{pct:+6.1f}%" if pct is not None else "  -  "
                # Highlight regressions (M3, M5 increase; M6 deviation from 1)
                marker = ""
                if c.startswith("M3_") and pct is not None and pct > 30: marker = " *"
                if c.startswith("M5_") and pct is not None and pct > 30: marker = " *"
                if c == "M6_visual_ratio":
                    if abs(fn - 1.0) > 0.2 and abs(fn - 1.0) > abs(fo - 1.0): marker = " *"
                print(f"{label:<{w}} {c:<22} {fmt(ov):>10} {fmt(nv):>10} {delta:>+10.3f} {pct_str:>8}{marker}")
                label = ""
            except (ValueError, TypeError):
                continue
        print()

if __name__ == "__main__":
    main()
