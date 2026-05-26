#!/usr/bin/env python3
"""Build an SVG plot of S7 C4 player rigidbody vs visual vs engine wallclock,
keyed on CLIENT-TICK (the same number shown on the in-video HUD). Mirrors the
multi-panel style of TestResults/Quantitative/*/dashboard.svg.

Usage: python plot_player_smoothing.py [path/to/client.log] [out.svg]
"""
import os, re, sys, glob
from datetime import datetime, timedelta

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# [DATETIME] ... [PRED-REG] tick=N eid=1 input=... pos=(X,Y,Z) vel=(VX,VY,VZ) angvel=(...)
BODY_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[PRED-REG\] tick=(\d+) eid=1\s.*?"
    r"pos=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\)\s+"
    r"vel=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\)"
)
# [DATETIME] ... [SMOOTH-FRAME] body=1 pf=N clientTick=T ... raw=(X,Y,Z) ... vis=(VX,VY,VZ) ...
# clientTick is the network tick the HUD would have displayed at this same
# _Process call — i.e. the tick number visible in the MP4 frame this log line
# corresponds to. Plot uses clientTick (not the engine physics frame counter)
# as the x-axis so the plot's x-coordinates line up exactly with the in-video
# HUD numbers the user reads.
VIS_RX = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[SMOOTH-FRAME\] body=1 pf=\d+ clientTick=(-?\d+) .*?"
    r"raw=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\) "
    r".*?"
    r"vis=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\)"
)
# Fallback for logs predating the clientTick tag — pair the SMOOTH-FRAME
# timestamp to the nearest PRED-REG via timestamp instead.
VIS_RX_LEGACY = re.compile(
    r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?"
    r"\[SMOOTH-FRAME\] body=1 pf=\d+ dt=.*?"
    r"raw=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\) "
    r".*?"
    r"vis=\((-?\d+\.\d+),(-?\d+\.\d+),(-?\d+\.\d+)\)"
)
# [PRED-ROLLBACK] tick=N entities=E resimTicks=R listenServer=...
ROLLBACK_RX = re.compile(
    r"\[PRED-ROLLBACK\] tick=(\d+) entities=\d+ resimTicks=(\d+)"
)


def find_log():
    pat = os.path.join(REPO, "tests", "TestResults", "Quantitative", "*", "S7-MultiBodyChaos.C4.client.log")
    matches = sorted(glob.glob(pat))
    return matches[-1] if matches else None


def parse_monotone(ts, prev):
    dt = datetime.strptime(ts, "%Y-%m-%dT%H:%M:%S.%f")
    if prev is not None:
        while dt < prev:
            dt = dt + timedelta(seconds=60)
    return dt


def parse(path):
    # body: per-CLIENT-TICK PRED-REG (live prediction). One entry per tick.
    # rendered: per-_Process SMOOTH-FRAME (what the MP4 actually captured),
    #           with (clientTick, body_z_at_render, vis_z, dt) so we know
    #           the body pose AND visual pose at every rendered frame in
    #           the video.
    body = []
    rendered = []
    rollbacks = []
    seen_tick = set()
    prev_body_dt = None
    prev_vis_dt = None
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            m = BODY_RX.search(line)
            if m:
                tick = int(m.group(2))
                if tick in seen_tick:
                    continue
                seen_tick.add(tick)
                dt = parse_monotone(m.group(1), prev_body_dt)
                prev_body_dt = dt
                body.append((tick, dt, float(m.group(4)), float(m.group(5)), float(m.group(8))))
                continue
            m = VIS_RX.search(line)
            if m:
                dt = parse_monotone(m.group(1), prev_vis_dt)
                prev_vis_dt = dt
                client_tick = int(m.group(2))
                rendered_body_z = float(m.group(5))
                vis_z = float(m.group(8))
                rendered.append((client_tick, dt, rendered_body_z, vis_z))
                continue
            m = ROLLBACK_RX.search(line)
            if m:
                rollbacks.append((int(m.group(1)), int(m.group(2))))
    return body, rendered, rollbacks


def visual_by_tick_from_rendered(rendered):
    # Each rendered frame is tagged with the clientTick that the HUD would
    # have displayed at that frame. Pick the FIRST rendered frame for each
    # clientTick — that's what the user sees in the MP4 for the moment the
    # HUD says "tick = T".
    out = {}
    for client_tick, _, _, vis_z in rendered:
        if client_tick not in out:
            out[client_tick] = vis_z
    return out


def rendered_body_by_tick(rendered):
    out = {}
    for client_tick, _, rendered_body_z, _ in rendered:
        if client_tick not in out:
            out[client_tick] = rendered_body_z
    return out


def render_svg(body, rendered, rendered_vis_by_tick, rendered_body_by_tick, rollbacks, out_path):
    if not body:
        print("no body data"); return

    ticks = [t for t, *_ in body]
    body_y = [(t, y) for t, _, y, _, _ in body]
    # Live prediction body Z (sampled per CLIENT-TICK from PRED-REG, after each
    # SpaceStep). This is the rigidbody state immediately after physics for
    # that tick — the "ground truth" of where the player ended up.
    body_z_live = [(t, z) for t, _, _, z, _ in body]
    vel_z = [(t, v) for t, _, _, _, v in body]
    # Body Z and visual Z AS RENDERED (= as captured into the MP4). For each
    # clientTick that the HUD displayed, the FIRST captured frame's body and
    # visual pos. These can differ from the live-tick values above: during
    # engine freezes the latest rendered frame may have stale state, and
    # during normal play the smoother actively keeps visual offset from body.
    body_z = [(t, rendered_body_by_tick[t]) for t in ticks if t in rendered_body_by_tick]
    vis_z = [(t, rendered_vis_by_tick[t]) for t in ticks if t in rendered_vis_by_tick]

    # Derived visual velocity: Δvis_z / Δt between successive rendered frames.
    # Since the smoother writes Visual.GlobalPosition once per physics tick,
    # visual.LinearVelocity isn't a Godot-exposed quantity — reconstruct it from
    # the captured visual.z series at known render-frame timestamps. Skip pairs
    # where Δt is implausibly large (engine freeze: > 0.2 s between frames)
    # since dividing across a freeze gives a misleading "average" velocity that
    # spans many physics ticks.
    rendered_sorted = sorted(rendered, key=lambda r: r[1])  # by wallclock
    vis_vel = []
    body_vel_rendered = []  # Δbody_z / Δt between rendered frames — captures rollback teleports
    for i in range(1, len(rendered_sorted)):
        t_prev, dt_prev, body_z_prev, vz_prev = rendered_sorted[i-1]
        t_now,  dt_now,  body_z_now,  vz_now  = rendered_sorted[i]
        dt = (dt_now - dt_prev).total_seconds()
        if dt <= 0 or dt > 0.2:
            continue
        vis_vel.append((t_now, (vz_now - vz_prev) / dt))
        body_vel_rendered.append((t_now, (body_z_now - body_z_prev) / dt))
    # tick-gap ms keyed on tick (only consecutive ticks)
    tick_gap = []
    prev_t, prev_dt = None, None
    for t, dt, *_ in body:
        if prev_t is not None and t == prev_t + 1:
            gap = (dt - prev_dt).total_seconds() * 1000.0
            tick_gap.append((t, gap))
        prev_t, prev_dt = t, dt

    PANEL_COLORS = {
        "rigidbody": "#1f77b4",
        "visual":    "#d62728",
        "y":         "#1f77b4",
        "vel":       "#2ca02c",
        "gap":       "#ff7f0e",
        "marker":    "#9467bd",
    }

    panels = [
        ("Z position (m)  -- AS CAPTURED IN THE MP4: rigidbody.z (solid) vs visual.z (dashed); -Z = forward",
            [("rigidbody.z (rendered)", PANEL_COLORS["rigidbody"], body_z,      False),
             ("visual.z (rendered)",    PANEL_COLORS["visual"],    vis_z,       True),
             ("rigidbody.z (live)",     "#7fbf7f",                 body_z_live, False)]),
        ("Y position (m)  -- ground at -2.0",
            [("rigidbody.y", PANEL_COLORS["y"], body_y, False)]),
        ("vel.z (m/s)  -- natural walk = -5.0; live (PRED-REG) is LinearVelocity, rendered is Δpos/Δt between rendered frames",
            [("rigidbody vel.z (live)",        PANEL_COLORS["vel"],     vel_z,             False),
             ("rigidbody vel.z (rendered Δ/Δt)", "#7fbf7f",             body_vel_rendered, False),
             ("visual vel.z (rendered Δ/Δt)",    PANEL_COLORS["visual"], vis_vel,           True)]),
        ("wallclock per CLIENT-TICK (ms)  -- 16.7 = real-time; spikes = engine freezes from rollback+resim",
            [("tick gap ms", PANEL_COLORS["gap"], tick_gap, False)]),
    ]

    # X axis: CLIENT-TICK range, with explicit tick-number labels.
    tick_min = ticks[0]
    tick_max = ticks[-1]

    W = 1600
    panel_h = 160
    pad_l, pad_r, pad_t, pad_b, pad_between = 80, 240, 60, 60, 24
    H = pad_t + len(panels) * panel_h + (len(panels)-1) * pad_between + pad_b
    plot_w = W - pad_l - pad_r

    def px(t):
        return pad_l + (t - tick_min) / max(1, tick_max - tick_min) * plot_w

    svg = ['<?xml version="1.0" encoding="UTF-8"?>']
    svg.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">')
    svg.append(f'<rect width="100%" height="100%" fill="#ffffff" />')
    svg.append('<style>')
    svg.append('.title    { font: 18px sans-serif; fill: #111; font-weight: bold; }')
    svg.append('.subtitle { font: 11px sans-serif; fill: #444; }')
    svg.append('.panel-bg { fill: #fafafa; stroke: #cccccc; }')
    svg.append('.panel-title { font: 12px sans-serif; fill: #222; font-weight: bold; }')
    svg.append('.y-label  { font: 10px monospace; fill: #666; text-anchor: end; }')
    svg.append('.x-label  { font: 10px monospace; fill: #444; text-anchor: middle; }')
    svg.append('.x-axis-title { font: 11px sans-serif; fill: #222; font-weight: bold; text-anchor: middle; }')
    svg.append('.legend   { font: 11px monospace; fill: #222; }')
    svg.append('.marker-label { font: 9px monospace; fill: #9467bd; text-anchor: middle; }')
    svg.append('</style>')
    svg.append(f'<text x="{W//2}" y="22" text-anchor="middle" class="title">S7-MultiBodyChaos C4 -- player rigidbody vs visual vs engine wallclock</text>')
    svg.append(f'<text x="{W//2}" y="40" text-anchor="middle" class="subtitle">X axis: CLIENT-TICK (matches HUD number in recorded MP4). Markers: PRED-ROLLBACK events (label = resim depth).</text>')

    # Tick labels on x-axis (every ~100 ticks)
    tick_step = max(50, ((tick_max - tick_min) // 12 // 50) * 50)
    if tick_step <= 0: tick_step = 100
    x_axis_ticks = list(range(((tick_min // tick_step) + 1) * tick_step, tick_max + 1, tick_step))

    for idx, (title, series) in enumerate(panels):
        py_top = pad_t + idx * (panel_h + pad_between)
        py_bot = py_top + panel_h
        all_v = []
        for _, _, vals, _ in series:
            all_v.extend(v for _, v in vals)
        if not all_v: continue
        vmin, vmax = min(all_v), max(all_v)
        if vmax == vmin: vmax = vmin + 1
        # Add 10% top/bottom padding
        pad_v = (vmax - vmin) * 0.08
        vmin_pad = vmin - pad_v
        vmax_pad = vmax + pad_v

        def py(v):
            return py_bot - (v - vmin_pad) / (vmax_pad - vmin_pad) * (py_bot - py_top)

        svg.append(f'<rect class="panel-bg" x="{pad_l}" y="{py_top}" width="{plot_w}" height="{panel_h}" />')
        svg.append(f'<text x="{pad_l}" y="{py_top - 6}" class="panel-title">{title}</text>')

        # Gridlines + y-labels at min, max, midpoints
        # Choose 4 y-labels
        for frac in (0.0, 0.25, 0.5, 0.75, 1.0):
            v = vmin_pad + frac * (vmax_pad - vmin_pad)
            yy = py(v)
            svg.append(f'<line x1="{pad_l}" y1="{yy:.1f}" x2="{pad_l+plot_w}" y2="{yy:.1f}" stroke="#dddddd" stroke-dasharray="2,2" stroke-width="0.5"/>')
            svg.append(f'<text x="{pad_l-6}" y="{yy+3:.1f}" class="y-label">{v:.2f}</text>')

        # Markers (vertical lines) — only on the topmost panel for clarity, with rollback depth labels
        for rb_tick, rb_resim in rollbacks:
            if tick_min <= rb_tick <= tick_max:
                xx = px(rb_tick)
                svg.append(f'<line x1="{xx:.1f}" y1="{py_top}" x2="{xx:.1f}" y2="{py_bot}" stroke="{PANEL_COLORS["marker"]}" stroke-dasharray="2,2" stroke-width="0.5" opacity="0.4"/>')

        for sidx, (label, color, vals, dashed) in enumerate(series):
            if not vals: continue
            pts = " ".join(f"{px(t):.1f},{py(v):.1f}" for t, v in vals)
            dash = ' stroke-dasharray="6,3"' if dashed else ''
            svg.append(f'<polyline fill="none" stroke="{color}" stroke-width="1.4" points="{pts}"{dash}/>')
            ly = py_top + 16 + sidx * 16
            svg.append(f'<line x1="{pad_l + plot_w + 10}" y1="{ly}" x2="{pad_l + plot_w + 40}" y2="{ly}" stroke="{color}" stroke-width="2"{dash}/>')
            svg.append(f'<text x="{pad_l + plot_w + 45}" y="{ly+4}" class="legend">{label}</text>')

        # X-axis labels under the LAST panel only
        is_last = (idx == len(panels) - 1)
        for xt in x_axis_ticks:
            xx = px(xt)
            svg.append(f'<line x1="{xx:.1f}" y1="{py_bot-3}" x2="{xx:.1f}" y2="{py_bot+3}" stroke="#888" stroke-width="0.8"/>')
            if is_last:
                svg.append(f'<text x="{xx:.1f}" y="{py_bot + 16}" class="x-label">{xt}</text>')

    # X-axis title
    svg.append(f'<text x="{pad_l + plot_w / 2:.0f}" y="{H - 18}" class="x-axis-title">CLIENT-TICK (HUD number)</text>')

    # Marker legend
    legend_y = pad_t + 60
    svg.append(f'<text x="{pad_l + plot_w + 10}" y="{H - 30}" class="legend">{len(rollbacks)} rollbacks</text>')

    svg.append('</svg>')
    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(svg))


def main():
    log = sys.argv[1] if len(sys.argv) > 1 else find_log()
    if not log:
        print("no log found"); sys.exit(1)
    body, rendered, rollbacks = parse(log)
    vis_by_tick = visual_by_tick_from_rendered(rendered)
    body_by_tick_rendered = rendered_body_by_tick(rendered)
    print(f"parsed: body ticks={len(body)} rendered samples={len(rendered)} rollbacks={len(rollbacks)} visual_by_tick={len(vis_by_tick)}")

    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(log), "S7-MultiBodyChaos.C4.player_smoothing.svg")
    render_svg(body, rendered, vis_by_tick, body_by_tick_rendered, rollbacks, out)
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
