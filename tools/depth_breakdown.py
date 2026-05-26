#!/usr/bin/env python3
"""Decompose rollback depth into source terms using both client and server
logs. Validates the formula `depth = (server_tick_now - snapshot.tick) + (clientTick - server_tick_now)`
and identifies where MonkeNet's effective lead differs from the theoretical
`latency + jitter + margin`.

Usage: python depth_breakdown.py <run_folder> <condition>
"""
import os, re, sys
from datetime import datetime

TX_RX = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[NET-SNAP-TX\] tick=(\d+)")
RX_RX = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[NET-SNAP-RX\] tick=(\d+).*?curTick=(-?\d+) rawTick=(-?\d+) rawAge=(-?\d+) avgLat=(-?\d+) jitter=(-?\d+) depth=(-?\d+)")
SYNC_RX = re.compile(r"\[CLOCK-SYNC-RX\] sample=(\d+) rttMs=(\d+) halfRttMs=(\d+) halfRttTicks=(\d+) srvTick=(\d+) cliTick=(\d+)")


def ts(s):
    return datetime.strptime(s, "%Y-%m-%dT%H:%M:%S.%f")


def main():
    run = sys.argv[1]
    cond = sys.argv[2]
    client = os.path.join(run, f"S7-MultiBodyChaos.{cond}.client.log")
    server = os.path.join(run, f"S7-MultiBodyChaos.{cond}.server.log")
    if not (os.path.exists(client) and os.path.exists(server)):
        print(f"missing client or server log for {cond}"); sys.exit(1)

    # Tick → server send timestamp
    tx_by_tick = {}
    with open(server, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = TX_RX.search(line)
            if m: tx_by_tick[int(m.group(2))] = ts(m.group(1))

    # Per-snapshot reception event, with all client clock state attached
    rx = []
    with open(client, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = RX_RX.search(line)
            if not m: continue
            recv_ts = ts(m.group(1))
            tick = int(m.group(2))
            rx.append({
                "recv": recv_ts,
                "tick": tick,
                "curTick": int(m.group(3)),
                "rawTick": int(m.group(4)),
                "rawAge": int(m.group(5)),
                "avgLat": int(m.group(6)),
                "jitter": int(m.group(7)),
                "depth": int(m.group(8)),
            })

    # Clock-sync samples — to see what the estimator currently believes about latency
    syncs = []
    with open(client, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = SYNC_RX.search(line)
            if m:
                syncs.append({
                    "sample": int(m.group(1)),
                    "rttMs": int(m.group(2)),
                    "halfRttMs": int(m.group(3)),
                    "halfRttTicks": int(m.group(4)),
                })
    if syncs:
        rtts = [s["rttMs"] for s in syncs[5:]]  # skip cold-start
        halfs = [s["halfRttMs"] for s in syncs[5:]]
        if rtts:
            print(f"\n=== {cond} clock-sync samples ({len(rtts)} steady-state) ===")
            print(f"  Reported RTT (ms): min={min(rtts)} median={sorted(rtts)[len(rtts)//2]} max={max(rtts)}")
            print(f"  half-RTT (ms):     min={min(halfs)} median={sorted(halfs)[len(halfs)//2]} max={max(halfs)}")
            print(f"  half-RTT (ticks):  median={sorted([s['halfRttTicks'] for s in syncs[5:]])[len(rtts)//2]}")

    # For each received snapshot, look up server send time and compute true
    # one-way latency from the wall-clock difference (same machine, same OS
    # clock → safe to subtract).
    aligned = []
    for r in rx:
        if r["tick"] not in tx_by_tick: continue
        send_ts = tx_by_tick[r["tick"]]
        one_way_ms = (r["recv"] - send_ts).total_seconds() * 1000
        one_way_ticks = one_way_ms / (1000.0 / 60.0)
        aligned.append({**r, "send": send_ts, "one_way_ms": one_way_ms, "one_way_ticks": one_way_ticks})

    if not aligned:
        print(f"\n=== {cond}: no snapshot TX↔RX matches ===")
        return

    n = len(aligned)
    # Throw away cold-start (first 5%)
    steady = aligned[max(int(n*0.05), 1):]
    if not steady: steady = aligned

    one_ways = sorted([a["one_way_ticks"] for a in steady])
    depths   = sorted([a["depth"]         for a in steady])
    avg_lats = sorted([a["avgLat"]        for a in steady])
    raw_ages = sorted([a["rawAge"]        for a in steady])
    jitters  = sorted([a["jitter"]        for a in steady])

    def p(vs, q): return vs[min(int(len(vs)*q), len(vs)-1)]

    print(f"\n=== {cond}: matched {n} snapshots ({len(steady)} steady-state) ===")
    print(f"\n  REAL one-way latency (ticks, from wall-clock):")
    print(f"    min={one_ways[0]:.2f}  P50={p(one_ways,0.5):.2f}  P95={p(one_ways,0.95):.2f}  max={one_ways[-1]:.2f}")

    print(f"\n  ESTIMATED one-way latency (avgLatTicks):")
    print(f"    min={avg_lats[0]}  P50={p(avg_lats,0.5)}  P95={p(avg_lats,0.95)}  max={avg_lats[-1]}")

    print(f"\n  raw-age (rawTick - snapshot.tick, = real one-way latency on client clock):")
    print(f"    min={raw_ages[0]}  P50={p(raw_ages,0.5)}  P95={p(raw_ages,0.95)}  max={raw_ages[-1]}")

    print(f"\n  jitter (ticks):")
    print(f"    min={jitters[0]}  P50={p(jitters,0.5)}  P95={p(jitters,0.95)}  max={jitters[-1]}")

    print(f"\n  ROLLBACK DEPTH (curTick - snapshot.tick):")
    print(f"    min={depths[0]}  P50={p(depths,0.5)}  P95={p(depths,0.95)}  max={depths[-1]}")

    # Decomposition check: depth should equal rawAge + (curTick - rawTick) =
    # rawAge + avgLat + jitter + margin
    print(f"\n  FORMULA CHECK on median snapshot:")
    mid = steady[len(steady)//2]
    print(f"    rawAge (true incoming latency, ticks)       = {mid['rawAge']}")
    print(f"    + avgLat (estimated outbound latency)       = {mid['avgLat']}")
    print(f"    + jitter (estimated outbound jitter)        = {mid['jitter']}")
    print(f"    + margin (always 0)                         = 0")
    print(f"    = {mid['rawAge'] + mid['avgLat'] + mid['jitter']}")
    print(f"    actual depth measured                       = {mid['depth']}")


if __name__ == "__main__":
    main()
