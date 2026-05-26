#!/usr/bin/env python3
"""Find the player visual's screen-position in a sequence of MP4 frames.
The Knight rig has a distinct dark-blue body that contrasts against the
light gray ground — find the centroid of dark-blue pixels.
"""
import os, sys, struct, zlib

def read_png(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} not PNG")
    idx = 8
    width = height = bit_depth = color_type = 0
    raw = b""
    while idx < len(data):
        length = struct.unpack(">I", data[idx:idx+4])[0]
        chunk_type = data[idx+4:idx+8]
        chunk_data = data[idx+8:idx+8+length]
        if chunk_type == b"IHDR":
            width, height, bit_depth, color_type = struct.unpack(">IIBB", chunk_data[:10])
        elif chunk_type == b"IDAT":
            raw += chunk_data
        elif chunk_type == b"IEND":
            break
        idx += 8 + length + 4
    decompressed = zlib.decompress(raw)
    if color_type == 2: bpp = 3
    elif color_type == 6: bpp = 4
    else: raise ValueError(f"unsupported color_type {color_type}")
    stride = width * bpp
    pixels = bytearray(width * height * bpp)
    prev_row = bytearray(stride)
    for y in range(height):
        filter_type = decompressed[y * (stride + 1)]
        row_data = decompressed[y * (stride + 1) + 1 : (y + 1) * (stride + 1)]
        cur_row = bytearray(stride)
        for x in range(stride):
            a = cur_row[x - bpp] if x >= bpp else 0
            b = prev_row[x]
            c = prev_row[x - bpp] if x >= bpp else 0
            byte = row_data[x]
            if filter_type == 0: cur_row[x] = byte
            elif filter_type == 1: cur_row[x] = (byte + a) & 0xFF
            elif filter_type == 2: cur_row[x] = (byte + b) & 0xFF
            elif filter_type == 3: cur_row[x] = (byte + (a + b) // 2) & 0xFF
            elif filter_type == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                if pa <= pb and pa <= pc: pr = a
                elif pb <= pc: pr = b
                else: pr = c
                cur_row[x] = (byte + pr) & 0xFF
            else:
                raise ValueError(f"unsupported filter {filter_type}")
        pixels[y * stride : (y + 1) * stride] = cur_row
        prev_row = cur_row
    return width, height, bpp, pixels


def player_centroid(path):
    """Find centroid of pixels that look like Knight body (dark blue-purple).
    Knight visual uses dark armor that's distinguishable from the gray floor
    and green cubes. Pixels where R<80, G<80, B>30, B<140 approximately
    capture the knight body."""
    w, h, bpp, px = read_png(path)
    sx = sy = n = 0
    # Skip top 30 rows (HUD text area)
    for y in range(30, h):
        for x in range(w):
            i = (y * w + x) * bpp
            r, g, b = px[i], px[i+1], px[i+2]
            # Dark blueish-purple armor on knight
            if r < 85 and g < 85 and b > 35 and b < 150 and (b - r) > -20:
                sx += x; sy += y; n += 1
    if n == 0: return None
    return (sx / n, sy / n, n)


if __name__ == "__main__":
    for path in sys.argv[1:]:
        c = player_centroid(path)
        name = os.path.basename(path)
        if c: print(f"{name}: pixel center ({c[0]:.1f}, {c[1]:.1f})  n={c[2]}")
        else: print(f"{name}: no match")
