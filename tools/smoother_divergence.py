#!/usr/bin/env python3
"""Diagnostic: parse SMOOTH-FRAME logs for the rigid-body player (body=1) and
produce (a) a CSV with one row per render frame and (b) a multi-panel SVG plot
of body vs visual position and velocity. Annotates engine freezes (large dt
gaps between consecutive render frames) and offset spikes — both signatures of
the post-resim-divergence bug.

Usage: python smoother_divergence.py <client.log> <out_dir>

Produces in <out_dir>:
  smoother_divergence.csv
  smoother_divergence.svg
"""
import os, re, sys, math

BODY_ID = "1"  # rigid-body player

FRAME_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[SMOOTH-FRAME\] body=" + BODY_ID + r" pf=(\d+) clientTick=(-?\d+) dt=([\d.]+) "
    r"pif=[\d.]+ raw=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r".*?vis=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r".*?vel=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)"
)
FIXUP_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[SMOOTH-FIXUP\] body=" + BODY_ID + r" "
    r"preVis=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"postBody=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"newOffset=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)"
)
REG_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-REG\] tick=(\d+) eid=1 "
)
# Snapshot arrival: [NET-SNAP-RX] tick=N entities=E (last=...)
SNAP_RX_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[NET-SNAP-RX\] tick=(\d+) entities=\d+"
)
# Snapshot dropped at packet layer: out-of-order tick (older than _lastTickReceived)
SNAP_OOO_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[NET-SNAP-RX\] dropped out-of-order tick=(\d+)"
)
# Snapshot accepted by net layer but no matching PredictedState (trimmed by cap
# or never registered): [PRED-CHECK] tick=N MISSED-LOCAL-STATE
SNAP_MISSED_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-CHECK\] tick=(\d+) MISSED-LOCAL-STATE"
)
# Snap-on-overflow: snapshot was older than rollback cap, body teleported to
# auth pose without a forward resim. Counted separately from MISSED-LOCAL-STATE
# (which is now reserved for genuine "no entry registered" events, e.g.
# pre-spawn / spectator).
SNAP_OVERFLOW_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-SNAP-OVERFLOW\] tick=(\d+)"
)
# Per-entity auth pose logged inside SnapToAuthOverflow. Used to plot the
# server-truth trajectory alongside the client-side render.
SNAP_OVERFLOW_AUTH_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-SNAP-OVERFLOW-ENTITY\] tick=(\d+) eid=1 "
    r"authPos=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"authVel=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)"
)
# Per-entity auth pose from a normal rollback path (eid=1 only).
RECONCILE_AUTH_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-RECONCILE\] tick=(\d+) eid=1 -> auth=eid=1 "
    r"pos=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) "
    r"vel=\((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\)"
)
# Reconcile fired for own player: [PRED-CHECK] tick=N eid=1 MISPREDICTED -> rollback
RECONCILE_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-CHECK\] tick=(\d+) eid=1 MISPREDICTED"
)
# Rollback: [PRED-ROLLBACK] tick=N entities=E resimTicks=R listenServer=...
ROLLBACK_DEPTH_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-ROLLBACK\] tick=(\d+) entities=\d+ resimTicks=(\d+)"
)


def parse(log_path):
    frames = []  # (ts_sec, pf, clientTick, dt, body.z, body.vel.z, vis.z)
    fixups = []  # (ts_sec, preVis.z, postBody.z, offset.z)
    reg_ticks = []  # (ts_sec, tick)
    snap_rx = []  # (ts_sec, snapshot_tick)
    snap_ooo = []  # (ts_sec, snapshot_tick) — out-of-order, dropped at packet layer
    snap_missed = []  # (ts_sec, snapshot_tick) — accepted but no matching predicted entry
    snap_overflow = []  # (ts_sec, snapshot_tick) — snap-on-overflow (cap-trimmed)
    auth_poses = []  # (ts_sec, snapshot_tick, auth.z, auth.vel.z) — server-truth for player
    reconciles = []  # (ts_sec, snapshot_tick)
    rollbacks = []  # (ts_sec, reconcile_tick, depth)
    t0 = None

    def ts(s):
        # H:MM:SS.mmm relative seconds since first sample
        h, m, rest = s.split("T")[1].split(":")
        return int(h) * 3600 + int(m) * 60 + float(rest)

    with open(log_path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = FRAME_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                frames.append((
                    t - t0, int(m.group(2)), int(m.group(3)), float(m.group(4)),
                    float(m.group(7)), float(m.group(13)), float(m.group(10)),
                ))
                continue
            m = FIXUP_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                fixups.append((
                    t - t0, float(m.group(4)), float(m.group(7)), float(m.group(10)),
                ))
                continue
            m = REG_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                reg_ticks.append((t - t0, int(m.group(2))))
                continue
            m = SNAP_RX_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                snap_rx.append((t - t0, int(m.group(2))))
                continue
            m = SNAP_OOO_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                snap_ooo.append((t - t0, int(m.group(2))))
                continue
            m = SNAP_MISSED_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                snap_missed.append((t - t0, int(m.group(2))))
                continue
            m = SNAP_OVERFLOW_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                snap_overflow.append((t - t0, int(m.group(2))))
                continue
            m = SNAP_OVERFLOW_AUTH_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                auth_poses.append((t - t0, int(m.group(2)), float(m.group(5)), float(m.group(8))))
                continue
            m = RECONCILE_AUTH_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                auth_poses.append((t - t0, int(m.group(2)), float(m.group(5)), float(m.group(8))))
                continue
            m = RECONCILE_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                reconciles.append((t - t0, int(m.group(2))))
                continue
            m = ROLLBACK_DEPTH_RX.search(line)
            if m:
                t = ts(m.group(1))
                if t0 is None:
                    t0 = t
                rollbacks.append((t - t0, int(m.group(2)), int(m.group(3))))
    return frames, fixups, reg_ticks, snap_rx, snap_ooo, snap_missed, snap_overflow, auth_poses, reconciles, rollbacks


def write_csv(frames, path):
    # Derive visual velocity from frame-to-frame visual position delta /
    # render-frame dt. This is what the eye actually perceives as visual speed,
    # so plotting it directly is the cleanest way to flag visual jumps.
    with open(path, "w", encoding="utf-8") as f:
        f.write("rel_sec,pf,client_tick,dt,body_z,body_vel_z,vis_z,vis_vel_z,offset_z\n")
        prev = None
        for row in frames:
            rel, pf, ct, dt, bz, bvz, vz = row
            if prev is None or dt <= 0:
                vvz = ""
            else:
                vvz = f"{(vz - prev[6]) / dt:.4f}"
            off = vz - bz
            f.write(f"{rel:.4f},{pf},{ct},{dt:.5f},{bz:.5f},{bvz:.4f},{vz:.5f},{vvz},{off:.4f}\n")
            prev = row


def find_problems(frames, freeze_dt=0.05, offset_m=0.25):
    """Return list of (rel_sec, clientTick, kind, magnitude) for diagnostic
    annotation. freeze_dt = render frame longer than 50 ms; offset_m = visual
    more than 25 cm away from body."""
    out = []
    for rel, pf, ct, dt, bz, bvz, vz in frames:
        if dt > freeze_dt:
            out.append((rel, ct, "freeze", dt * 1000.0))
        if abs(vz - bz) > offset_m:
            out.append((rel, ct, "offset", abs(vz - bz)))
    return out


# -------------------------- SVG plotting --------------------------
def svg(frames, fixups, problems, reg_ticks, snap_rx, snap_ooo, snap_missed, snap_overflow, reconciles, rollbacks, path):
    # Four stacked panels: position (body + vis), velocity (body + vis frame-
    # derived), render-frame dt, and snapshot event timeline. X axis = seconds.
    if not frames:
        with open(path, "w") as f:
            f.write("<svg xmlns='http://www.w3.org/2000/svg'><text>no frames</text></svg>")
        return

    W, H = 1600, 1100
    M = {"l": 70, "r": 30, "t": 30, "b": 50}
    PANEL_H = (H - M["t"] - M["b"]) // 4
    x_min = frames[0][0]
    x_max = frames[-1][0]
    if x_max == x_min: x_max = x_min + 1

    def x(s): return M["l"] + (s - x_min) / (x_max - x_min) * (W - M["l"] - M["r"])

    # Panel 1: position
    bz_min = min(r[4] for r in frames); bz_max = max(r[4] for r in frames)
    vz_min = min(r[6] for r in frames); vz_max = max(r[6] for r in frames)
    p1_min = min(bz_min, vz_min) - 0.5
    p1_max = max(bz_max, vz_max) + 0.5

    def y1(v):
        return M["t"] + PANEL_H - (v - p1_min) / (p1_max - p1_min) * (PANEL_H - 10)

    # Panel 2: velocity (body + frame-derived visual)
    vels_body = [r[5] for r in frames]
    vels_vis = []
    prev = None
    for r in frames:
        if prev is None or r[3] <= 0:
            vels_vis.append(0.0)
        else:
            vels_vis.append((r[6] - prev[6]) / r[3])
        prev = r
    p2_min = min(min(vels_body), min(vels_vis)) - 1.0
    p2_max = max(max(vels_body), max(vels_vis)) + 1.0
    p2_top = M["t"] + PANEL_H + 20

    def y2(v):
        return p2_top + PANEL_H - (v - p2_min) / (p2_max - p2_min) * (PANEL_H - 10)

    # Panel 3: dt + reg-tick gap markers
    p3_top = p2_top + PANEL_H + 20
    dt_max = max(r[3] for r in frames) * 1.1

    def y3(v):
        return p3_top + PANEL_H - v / dt_max * (PANEL_H - 10)

    def polyline(pts, color, w=1.5):
        s = " ".join(f"{px:.1f},{py:.1f}" for px, py in pts)
        return f"<polyline fill='none' stroke='{color}' stroke-width='{w}' points='{s}'/>"

    body_pts = [(x(r[0]), y1(r[4])) for r in frames]
    vis_pts = [(x(r[0]), y1(r[6])) for r in frames]
    bvel_pts = [(x(r[0]), y2(v)) for r, v in zip(frames, vels_body)]
    vvel_pts = [(x(r[0]), y2(v)) for r, v in zip(frames, vels_vis)]
    dt_pts = [(x(r[0]), y3(r[3])) for r in frames]

    # Panel 4: snapshot events timeline (binary lanes per event type)
    p4_top = p3_top + PANEL_H + 20
    snap_rx_y = p4_top + PANEL_H * 0.22
    reconcile_y = p4_top + PANEL_H * 0.44
    rollback_y = p4_top + PANEL_H * 0.65
    missed_y = p4_top + PANEL_H * 0.87
    # Rollback-depth secondary axis (right side) within panel 4
    rb_max = max([r[2] for r in rollbacks], default=10)

    parts = [f"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' font-family='monospace' font-size='11'>"]
    parts.append(f"<rect width='100%' height='100%' fill='white'/>")

    # panel backgrounds
    for top in (M["t"], p2_top, p3_top, p4_top):
        parts.append(f"<rect x='{M['l']}' y='{top}' width='{W-M['l']-M['r']}' height='{PANEL_H}' fill='none' stroke='#ccc'/>")

    # problem highlights (vertical bands for freezes, dots for offsets)
    for rel, ct, kind, mag in problems:
        px = x(rel)
        if kind == "freeze":
            parts.append(f"<line x1='{px:.1f}' y1='{M['t']}' x2='{px:.1f}' y2='{p3_top + PANEL_H}' stroke='#fdd' stroke-width='4' opacity='0.6'/>")
        elif kind == "offset":
            parts.append(f"<circle cx='{px:.1f}' cy='{y1(0)}' r='3' fill='red' opacity='0.6'/>")

    # fixup markers in position panel (purple triangles at preVis)
    for rel, pre, post, off in fixups:
        px = x(rel); py = y1(pre)
        parts.append(f"<polygon points='{px:.1f},{py-5:.1f} {px-4:.1f},{py+3:.1f} {px+4:.1f},{py+3:.1f}' fill='purple' opacity='0.7'/>")

    # data
    parts.append(polyline(body_pts, "#1f77b4", 2))  # body z = blue
    parts.append(polyline(vis_pts, "#d62728", 1.5))  # visual z = red
    parts.append(polyline(bvel_pts, "#1f77b4", 1.5))
    parts.append(polyline(vvel_pts, "#d62728", 1.5))
    parts.append(polyline(dt_pts, "#2ca02c", 1.2))

    # Panel 4: snapshot events
    # SNAP-RX (green dots, top lane)
    for rel, t in snap_rx:
        parts.append(f"<circle cx='{x(rel):.1f}' cy='{snap_rx_y:.1f}' r='2' fill='#2ca02c'/>")
    # RECONCILE (orange diamonds, second lane)
    for rel, t in reconciles:
        px, py = x(rel), reconcile_y
        parts.append(f"<polygon points='{px:.1f},{py-4:.1f} {px+4:.1f},{py:.1f} {px:.1f},{py+4:.1f} {px-4:.1f},{py:.1f}' fill='#ff7f0e'/>")
    # ROLLBACK depth as vertical bars (third lane, scaled by depth)
    for rel, ctick, depth in rollbacks:
        px = x(rel)
        h = (depth / max(1, rb_max)) * (PANEL_H * 0.30)
        parts.append(f"<rect x='{px-1.5:.1f}' y='{rollback_y - h:.1f}' width='3' height='{h:.1f}' fill='#9467bd' opacity='0.7'/>")
        if depth >= 10:
            parts.append(f"<text x='{px:.1f}' y='{rollback_y - h - 2:.1f}' text-anchor='middle' fill='#9467bd' font-size='9'>{depth}</text>")
    # MISSED-LOCAL-STATE (red X-marks, bottom lane)
    for rel, t in snap_missed:
        px, py = x(rel), missed_y
        parts.append(f"<line x1='{px-3:.1f}' y1='{py-3:.1f}' x2='{px+3:.1f}' y2='{py+3:.1f}' stroke='#d62728' stroke-width='1.5'/>")
        parts.append(f"<line x1='{px-3:.1f}' y1='{py+3:.1f}' x2='{px+3:.1f}' y2='{py-3:.1f}' stroke='#d62728' stroke-width='1.5'/>")
    # SNAP-OVERFLOW (cyan squares, shares missed lane)
    for rel, t in snap_overflow:
        px, py = x(rel), missed_y
        parts.append(f"<rect x='{px-3:.1f}' y='{py-3:.1f}' width='6' height='6' fill='#17becf' opacity='0.85'/>")
    # OUT-OF-ORDER drops (gray X-marks, sharing missed lane shifted up)
    for rel, t in snap_ooo:
        px, py = x(rel), missed_y - 8
        parts.append(f"<line x1='{px-3:.1f}' y1='{py-3:.1f}' x2='{px+3:.1f}' y2='{py+3:.1f}' stroke='#888' stroke-width='1.2'/>")
        parts.append(f"<line x1='{px-3:.1f}' y1='{py+3:.1f}' x2='{px+3:.1f}' y2='{py-3:.1f}' stroke='#888' stroke-width='1.2'/>")

    # lane labels (panel 4)
    parts.append(f"<text x='{M['l']+4}' y='{snap_rx_y+3:.1f}' fill='#2ca02c'>SNAP-RX (green) — {len(snap_rx)} arrived</text>")
    parts.append(f"<text x='{M['l']+4}' y='{reconcile_y+3:.1f}' fill='#ff7f0e'>RECONCILE (orange) — {len(reconciles)} fired</text>")
    parts.append(f"<text x='{M['l']+4}' y='{rollback_y+3:.1f}' fill='#9467bd'>ROLLBACK depth (purple bars, max={rb_max}t) — {len(rollbacks)} rollbacks</text>")
    parts.append(f"<text x='{M['l']+4}' y='{missed_y+3:.1f}' fill='#d62728'>MISSED-LOCAL-STATE red X={len(snap_missed)}  SNAP-OVERFLOW cyan ▪={len(snap_overflow)}  (out-of-order grey X above = {len(snap_ooo)})</text>")

    # axes labels
    parts.append(f"<text x='{M['l']}' y='{M['t']-8}' fill='black'>Position Z (m)  — body=blue  visual=red  ▼=SMOOTH-FIXUP preVis  pink-band=engine-freeze  red-dot=|vis-body|>0.25m</text>")
    parts.append(f"<text x='{M['l']}' y='{p2_top-8}' fill='black'>Velocity Z (m/s)  — body.LinearVelocity=blue  frame-derived visual velocity=red</text>")
    parts.append(f"<text x='{M['l']}' y='{p3_top-8}' fill='black'>Render-frame dt (s)  — &gt;50ms = engine freeze; PRED-REG tick gaps shown as text</text>")
    parts.append(f"<text x='{M['l']}' y='{p4_top-8}' fill='black'>Snapshot events — arrival vs reconcile vs rollback-depth vs dropped (post-cap)</text>")

    # y-axis ticks for position panel
    for v in range(int(p1_min), int(p1_max) + 1, max(1, int((p1_max - p1_min) / 8))):
        yy = y1(v)
        parts.append(f"<line x1='{M['l']-3}' y1='{yy:.1f}' x2='{M['l']}' y2='{yy:.1f}' stroke='#666'/>")
        parts.append(f"<text x='{M['l']-5}' y='{yy+3:.1f}' text-anchor='end' fill='#666'>{v}</text>")

    # x-axis seconds ticks (every second)
    for s in range(int(x_min), int(x_max) + 1):
        if s % 1 == 0:
            xx = x(s)
            for top in (M["t"], p2_top, p3_top, p4_top):
                parts.append(f"<line x1='{xx:.1f}' y1='{top + PANEL_H}' x2='{xx:.1f}' y2='{top + PANEL_H + 3}' stroke='#666'/>")
                parts.append(f"<text x='{xx:.1f}' y='{top + PANEL_H + 14}' text-anchor='middle' fill='#666'>{s}s</text>")

    # reg-tick gap annotations: where successive PRED-REG ticks differ by > 1
    gaps = []
    prev = None
    for rel, t in reg_ticks:
        if prev is not None and t - prev[1] > 1:
            gaps.append((rel, prev[1], t, t - prev[1] - 1))
        prev = (rel, t)
    for rel, prev_t, t, gap_size in gaps:
        if gap_size < 3: continue  # ignore tiny gaps
        px = x(rel)
        parts.append(f"<line x1='{px:.1f}' y1='{p3_top}' x2='{px:.1f}' y2='{p3_top + PANEL_H}' stroke='orange' stroke-width='1' stroke-dasharray='3,2'/>")
        parts.append(f"<text x='{px:.1f}' y='{p3_top + 12}' text-anchor='middle' fill='orange' font-size='9'>gap {gap_size}</text>")

    parts.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


# -------------------------- Clean monotonic-time-series view --------------
# The diagnostic SVG above shows the raw body trajectory, which zigzags up-
# and-down whenever a snap-on-overflow teleports the body backward to an
# older auth pose (visible as fast spikes vs the steady visual line). This
# "clean" view drops the raw body line entirely and renders only:
#
#   * visual.z over real time (the position the player actually sees) — fully
#     smooth thanks to PredictionVisualSmoothing3D's offset+decay absorbing
#     every snap.
#   * server-truth auth.z markers (one per snap-overflow / rollback event)
#     plotted as dots so the reader can see how closely the smoothed client
#     render tracks what the server thinks the player's position should be.
#
# Both series are inherently monotonic in this scenario (player moves
# continuously in -Z), so the line should flow cleanly down-and-to-the-right.
def svg_clean(frames, auth_poses, path, title="Smoothed render vs server-truth"):
    # X-axis is CLIENT TICK (= server-aligned simulation tick), NOT wall-clock
    # log time. The MonkeLogger flushes async, so wall-clock timestamps for
    # entries inside the same physics tick can be reordered by tens or hundreds
    # of milliseconds in the file. clientTick is monotonic-by-construction
    # (always advances by ≥ 0 each physics frame, never resets on resync —
    # ClientNetworkClock applies coarse corrections that step the tick forward
    # but never backward in steady state). Plotting against clientTick gives a
    # strictly monotonic X-axis that flows down-and-to-the-right cleanly, and
    # also puts the visual line and the auth dots on the same coordinate
    # system: visual.z at clientTick T is what the client renders for server
    # time T, and an auth pose for snapshot.tick T is exactly the server's
    # ground-truth position at server time T.
    if not frames:
        with open(path, "w") as f:
            f.write("<svg xmlns='http://www.w3.org/2000/svg'><text>no frames</text></svg>")
        return

    # Sort + dedupe by tick so the polyline has well-ordered points even when
    # the log lines arrived out of order. Within a single clientTick value we
    # may have multiple SMOOTH-FRAME entries (one per render frame during a
    # long tick); pick the latest reported vis.z (last writer wins).
    by_tick = {}
    for r in frames:
        by_tick[r[2]] = r  # r[2] = clientTick
    frames_sorted = sorted(by_tick.values(), key=lambda r: r[2])
    auth_sorted = sorted(auth_poses, key=lambda a: a[1])  # a[1] = snapshot tick

    W, H = 1600, 600
    M = {"l": 70, "r": 30, "t": 50, "b": 50}
    PANEL_H = H - M["t"] - M["b"]
    x_min = frames_sorted[0][2]
    x_max = frames_sorted[-1][2]
    if auth_sorted:
        x_min = min(x_min, auth_sorted[0][1])
        x_max = max(x_max, auth_sorted[-1][1])
    if x_max == x_min: x_max = x_min + 1

    def x(t): return M["l"] + (t - x_min) / (x_max - x_min) * (W - M["l"] - M["r"])

    vis_zs = [r[6] for r in frames_sorted]
    auth_zs = [a[2] for a in auth_sorted]
    all_zs = vis_zs + auth_zs
    y_min = min(all_zs) - 0.5
    y_max = max(all_zs) + 0.5

    def y(v): return M["t"] + PANEL_H - (v - y_min) / (y_max - y_min) * PANEL_H

    parts = [f"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' font-family='monospace' font-size='12'>"]
    parts.append(f"<rect width='100%' height='100%' fill='white'/>")
    parts.append(f"<text x='{W//2}' y='25' text-anchor='middle' font-size='14' font-weight='bold'>{title}</text>")
    # panel frame
    parts.append(f"<rect x='{M['l']}' y='{M['t']}' width='{W-M['l']-M['r']}' height='{PANEL_H}' fill='none' stroke='#aaa'/>")
    # Z gridlines (every meter)
    for z in range(int(y_min), int(y_max) + 1):
        yy = y(z)
        parts.append(f"<line x1='{M['l']}' y1='{yy:.1f}' x2='{W-M['r']}' y2='{yy:.1f}' stroke='#eee' stroke-width='0.5'/>")
        parts.append(f"<text x='{M['l']-5}' y='{yy+4:.1f}' text-anchor='end' fill='#666' font-size='10'>{z}m</text>")
    # Tick gridlines (every ~60 ticks = 1s of sim time at 60Hz). Pick step so
    # we get ~15 labelled gridlines across the panel regardless of run length.
    tick_span = x_max - x_min
    step = max(60, int(tick_span / 15 / 60) * 60)
    first = ((int(x_min) + step - 1) // step) * step
    for t in range(first, int(x_max) + 1, step):
        xx = x(t)
        parts.append(f"<line x1='{xx:.1f}' y1='{M['t']}' x2='{xx:.1f}' y2='{M['t']+PANEL_H}' stroke='#eee' stroke-width='0.5'/>")
        parts.append(f"<text x='{xx:.1f}' y='{M['t']+PANEL_H+14}' text-anchor='middle' fill='#666' font-size='10'>tick {t}</text>")
        parts.append(f"<text x='{xx:.1f}' y='{M['t']+PANEL_H+26}' text-anchor='middle' fill='#aaa' font-size='9'>({t/60.0:.1f}s)</text>")
    # visual.z line (red, what the player sees)
    vis_pts = " ".join(f"{x(r[2]):.1f},{y(r[6]):.1f}" for r in frames_sorted)
    parts.append(f"<polyline fill='none' stroke='#d62728' stroke-width='2' points='{vis_pts}'/>")
    # server-truth auth.z dots (green)
    for _, snap_tick, az, _ in auth_sorted:
        parts.append(f"<circle cx='{x(snap_tick):.1f}' cy='{y(az):.1f}' r='2.5' fill='#2ca02c' opacity='0.85'/>")
    # legend
    parts.append(f"<rect x='{W-280}' y='{M['t']+10}' width='270' height='44' fill='white' stroke='#ccc'/>")
    parts.append(f"<line x1='{W-270}' y1='{M['t']+22}' x2='{W-245}' y2='{M['t']+22}' stroke='#d62728' stroke-width='2'/>")
    parts.append(f"<text x='{W-240}' y='{M['t']+26}' fill='black'>client visual.z at clientTick</text>")
    parts.append(f"<circle cx='{W-257}' cy='{M['t']+42}' r='2.5' fill='#2ca02c'/>")
    parts.append(f"<text x='{W-240}' y='{M['t']+46}' fill='black'>server auth.z at snapshot.tick</text>")
    # axes
    parts.append(f"<text x='{M['l']}' y='{M['t']-8}' fill='black'>Z position (m) vs server-aligned tick — strictly monotonic X; visual and auth share the same tick coordinate so they should overlay where the client is tracking the server correctly.</text>")
    parts.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


def main():
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(1)
    log = sys.argv[1]
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    frames, fixups, regs, snap_rx, snap_ooo, snap_missed, snap_overflow, auth_poses, reconciles, rollbacks = parse(log)
    print(f"parsed {len(frames)} render frames, {len(fixups)} fixups, {len(regs)} reg ticks, "
          f"{len(snap_rx)} snap-rx, {len(snap_ooo)} out-of-order drops, "
          f"{len(snap_missed)} missed-local-state, {len(snap_overflow)} snap-overflow, "
          f"{len(auth_poses)} auth-pose samples, {len(reconciles)} reconciles, {len(rollbacks)} rollbacks")
    csv_path = os.path.join(out_dir, "smoother_divergence.csv")
    svg_path = os.path.join(out_dir, "smoother_divergence.svg")
    svg_clean_path = os.path.join(out_dir, "smoother_divergence_clean.svg")
    write_csv(frames, csv_path)
    problems = find_problems(frames)
    print(f"problems found: {sum(1 for p in problems if p[2]=='freeze')} freezes, {sum(1 for p in problems if p[2]=='offset')} offset events")
    svg(frames, fixups, problems, regs, snap_rx, snap_ooo, snap_missed, snap_overflow, reconciles, rollbacks, svg_path)
    # Determine scenario+condition for the clean view's title from the log filename
    base = os.path.basename(log).replace(".client.log", "").replace(".log", "")
    svg_clean(frames, auth_poses, svg_clean_path, title=f"{base} — smoothed render vs server-truth")

    # Summary stats
    offsets = [abs(r[6] - r[4]) for r in frames]
    if offsets:
        offsets_sorted = sorted(offsets)
        n = len(offsets_sorted)
        p50 = offsets_sorted[n // 2]
        p95 = offsets_sorted[int(n * 0.95)]
        p99 = offsets_sorted[int(n * 0.99)]
        mx = max(offsets)
        print(f"\n|vis - body| z-axis offset stats (meters):")
        print(f"  P50={p50:.4f}  P95={p95:.4f}  P99={p99:.4f}  MAX={mx:.4f}")
        print(f"  samples > 0.25m: {sum(1 for o in offsets if o > 0.25)}")
        print(f"  samples > 0.50m: {sum(1 for o in offsets if o > 0.50)}")
        print(f"  samples > 1.00m: {sum(1 for o in offsets if o > 1.00)}")

    dts = [r[3] for r in frames]
    if dts:
        dt_sorted = sorted(dts)
        print(f"\nrender-frame dt stats (ms):")
        print(f"  P50={dt_sorted[len(dts)//2]*1000:.1f}  P95={dt_sorted[int(len(dts)*0.95)]*1000:.1f}  P99={dt_sorted[int(len(dts)*0.99)]*1000:.1f}  MAX={max(dts)*1000:.1f}")
        print(f"  freezes >50ms: {sum(1 for d in dts if d > 0.05)}")
        print(f"  freezes >100ms: {sum(1 for d in dts if d > 0.1)}")

    # Reg-tick gap summary
    reg_gaps = []
    prev = None
    for rel, t in regs:
        if prev is not None: reg_gaps.append(t - prev - 1)
        prev = t
    if reg_gaps:
        big = sum(1 for g in reg_gaps if g >= 5)
        biggest = max(reg_gaps)
        print(f"\nPRED-REG tick gaps:")
        print(f"  largest skip: {biggest} ticks")
        print(f"  skips >= 5 ticks: {big}")
        print(f"  total skipped ticks: {sum(reg_gaps)}")

    print(f"\nwrote: {csv_path}")
    print(f"wrote: {svg_path}")


if __name__ == "__main__":
    main()
