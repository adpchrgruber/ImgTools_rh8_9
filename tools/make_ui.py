"""
Generates ImgTools' UI assets (standard library only):

  resources/ImgTools.ico          plug-in icon
  dist/icon.png                   package icon
  resources/ImgTools.rui          toolbar: ClownID, ClownIDCapture, ZDepth (right-click: ZDepthRange), ZDepthCapture,
                                  ShadowView (right-click: ImgToolsOptions), ShadowCapture,
                                  AOView (right-click: ImgToolsOptions), AOCapture,
                                  NormalView (right-click: ImgToolsOptions), NormalCapture

Run from the repository root:  python3 tools/make_ui.py
GUIDs are fixed so Rhino keeps the toolbar's position and visibility across updates.
"""
import base64
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# --- tiny PNG / ICO writer --------------------------------------------------

def png(w, h, rgba):
    raw = b''.join(b'\x00' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)

    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico(images):
    """images: list of (size, png bytes)."""
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for size, data in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return header + entries + b''.join(d for _, d in images)


# --- icon drawing (unit square, y down) -------------------------------------

def hexc(s):
    return tuple(int(s[i:i + 2], 16) for i in (1, 3, 5))


BLACK, WHITE = hexc('#000000'), hexc('#FFFFFF')
GREEN, PINK, BLUE = hexc('#26D609'), hexc('#FF4FA3'), hexc('#33A1FF')


def grey(level, v):
    """A depth grey, slightly lighter towards the bottom (nearer) of the shape."""
    g = max(0, min(255, round(level + (v - 0.5) * 70)))
    return (g, g, g)


def in_rounded_square(x, y, r=0.2):
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def depth_shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """Clown ID's motif as a depth pass: far shapes dark, near shapes light."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    col = None
    if 0.13 <= u <= 0.60 and 0.44 <= v <= 0.87:
        col = grey(85, v)
    if 0.29 <= u <= 0.51 and 0.13 <= v <= 0.62:
        col = grey(150, v)
    if (u - 0.69) ** 2 + (v - 0.60) ** 2 <= 0.19 ** 2:
        col = grey(225, v)
    return col


def clown_shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """The clown-pass motif: each shape one flat ID colour."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    col = None
    if 0.13 <= u <= 0.60 and 0.44 <= v <= 0.87:
        col = GREEN
    if 0.29 <= u <= 0.51 and 0.13 <= v <= 0.62:
        col = PINK
    if (u - 0.69) ** 2 + (v - 0.60) ** 2 <= 0.19 ** 2:
        col = BLUE
    return col


def shadow_shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """The motif's shadows thrown down-left by a light from the upper right: white on black."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    # shear the shapes along the light direction and keep only what falls outside them
    su, sv = u + 0.16, v - 0.16
    inside = (0.13 <= u <= 0.60 and 0.44 <= v <= 0.87) or (0.29 <= u <= 0.51 and 0.13 <= v <= 0.62) \
        or (u - 0.69) ** 2 + (v - 0.60) ** 2 <= 0.19 ** 2
    cast = (0.13 <= su <= 0.60 and 0.44 <= sv <= 0.87) or (0.29 <= su <= 0.51 and 0.13 <= sv <= 0.62) \
        or (su - 0.69) ** 2 + (sv - 0.60) ** 2 <= 0.19 ** 2
    if inside:
        return hexc('#6E6E6E')
    if cast and 0.0 <= v <= 1.0:
        return WHITE
    return None


def ao_shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """The motif in ambient occlusion: light shapes on a light ground, soft dark gradients
    where the shapes meet the ground and each other."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    if not (0 <= u <= 1 and 0 <= v <= 1):
        return None

    def rect_dist(a0, b0, a1, b1):
        du = max(a0 - u, 0, u - a1)
        dv = max(b0 - v, 0, v - b1)
        return (du * du + dv * dv) ** 0.5

    d_low = rect_dist(0.13, 0.44, 0.60, 0.87)
    d_tall = rect_dist(0.29, 0.13, 0.51, 0.62)
    d_ball = max(0.0, ((u - 0.69) ** 2 + (v - 0.60) ** 2) ** 0.5 - 0.19)
    inside = d_low == 0 or d_tall == 0 or d_ball == 0
    if inside:
        # faces: light grey, darker towards where the tall block meets the low one
        g = 200 if d_ball == 0 else 215
        if d_low == 0 and d_tall > 0:
            g -= round(90 * max(0, 1 - d_tall / 0.10))
        return (g, g, g)
    near = min(d_low, d_tall, d_ball)
    g = round(245 - 170 * max(0, 1 - near / 0.16) ** 2)
    return (g, g, g)


def normal_shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """The motif as a camera-space normal map: flat blue ground, each face coloured by its normal."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    if not (0 <= u <= 1 and 0 <= v <= 1):
        return None

    def enc(nx, ny, nz):
        l = (nx * nx + ny * ny + nz * nz) ** 0.5
        return tuple(round(255 * (c / l * 0.5 + 0.5)) for c in (nx, ny, nz))

    du, dv = (u - 0.69) / 0.19, (v - 0.60) / 0.19
    if du * du + dv * dv <= 1:
        return enc(du, -dv, max(0.0, 1 - du * du - dv * dv) ** 0.5)   # sphere
    if 0.29 <= u <= 0.51 and 0.13 <= v <= 0.62:
        return enc(-0.55, 0.25, 0.8)    # tall block, facing left and a little up
    if 0.13 <= u <= 0.60 and 0.44 <= v <= 0.87:
        return enc(0.0, 0.6, 0.8)       # low block, facing up
    return enc(0, 0, 1)                  # flat ground


def plugin_icon(x, y):
    """Three bands: clown, depth, shadow."""
    if not in_rounded_square(x, y):
        return None
    if x < 1 / 3:
        return clown_shapes(x, y) or BLACK
    if x < 2 / 3:
        return depth_shapes(x, y) or BLACK
    return shadow_shapes(x, y) or BLACK


def mode(shapes):
    def icon(x, y):
        if not in_rounded_square(x, y):
            return None
        return shapes(x, y) or BLACK
    return icon


def capture(shapes):
    def icon(x, y):
        return capture_icon(x, y, shapes)
    return icon


def capture_icon(x, y, shapes):
    if not in_rounded_square(x, y):
        return None
    # viewfinder corners
    a, b, t = 0.11, 0.37, 0.10
    for cx, cy in ((a, a), (1 - a, a), (a, 1 - a), (1 - a, 1 - a)):
        dx, dy = abs(x - cx), abs(y - cy)
        sx = 1 if cx < 0.5 else -1
        sy = 1 if cy < 0.5 else -1
        along_x = 0 <= (x - cx) * sx <= b - a and 0 <= (y - cy) * sy <= t
        along_y = 0 <= (y - cy) * sy <= b - a and 0 <= (x - cx) * sx <= t
        if along_x or along_y:
            return WHITE
    return shapes(x, y, (0.2, 0.2, 0.8, 0.8)) or BLACK


def render(fn, n, ss=8):
    out = bytearray()
    for py in range(n):
        for px in range(n):
            acc, hits = [0, 0, 0], 0
            for sy in range(ss):
                for sx in range(ss):
                    c = fn((px + (sx + .5) / ss) / n, (py + (sy + .5) / ss) / n)
                    if c:
                        acc = [acc[i] + c[i] for i in range(3)]
                        hits += 1
            out += bytes([acc[i] // hits if hits else 0 for i in range(3)] + [round(255 * hits / ss / ss)])
    return bytes(out)


def strip(icons, n):
    """Horizontal strip of icons, n×n each, as one PNG."""
    tiles = [render(fn, n) for fn in icons]
    rows = b''.join(b''.join(t[y * n * 4:(y + 1) * n * 4] for t in tiles) for y in range(n))
    return png(n * len(icons), n, rows)


# --- toolbar file ------------------------------------------------------------

FILE_ID = '485ca4bf-80b6-44a7-997e-aab71f8e8fd2'
MENU_ID = '39f5f716-5cbd-404e-b73a-3bb32932f896'
GROUP_ID = '7150e1ba-565b-46d2-b045-cc98cdde1358'
DOCK_ID = 'dae3736a-d7c5-4020-9b85-2464d144b39e'
GROUP_ITEM_ID = '312ac3cd-dcd8-4674-a33d-caf3ee11aca4'
TOOLBAR_ID = '3bb9f02a-5155-493b-b38f-e426f0871877'

BUTTONS = [
    # (item id, macro id, bitmap id, icon, script, name, tooltip)
    ('7b4c28f3-5ef8-4097-b8dd-8e4c60dc7d20', 'c630949d-947d-4262-9b06-2b4f4d1bd7f3',
     'a430581f-5f34-4b50-a219-c9fce38923f3', mode(clown_shapes), '! _ClownID', 'Clown ID',
     'Switch the active viewport to Clown ID and back'),
    ('ac494afc-3985-4107-b634-7416c8b22eda', 'b8f3e1d2-6a4c-4f7e-9d1b-2c5e8a7f3b61',
     'e2d7c9a4-1b3f-4e8d-a6c5-9f0b2d4e7a18', capture(clown_shapes), '! _ClownIDCapture', 'Clown ID Capture',
     'Save the active view as a clown-pass PNG with a JSON colour key'),
    ('dc31e337-0f8f-4eaf-9e72-e9de09ac8e88', '6420923d-59d3-45e4-b9f5-8b50f995b552',
     'f3e936d4-a1de-4a9a-8390-f40196ea4e9c', mode(depth_shapes), '! _ZDepth', 'Z Depth',
     'Switch the active viewport to Z Depth and back'),
    ('e9e207dc-3997-4d3e-babe-963b04530613', 'c706c1f9-358a-4362-bb2f-ebba50791989',
     '852e9120-76a6-4cb5-a43c-b5e6f69fdde3', capture(depth_shapes), '! _ZDepthCapture', 'Z Depth Capture',
     'Save the active view as a depth map: PNG, 16-bit PNG or EXR, at any size'),
    ('7d77c8eb-5a3b-4f71-83aa-f06a979e35ab', '110cac9f-b0b7-4761-a94f-946a2489d96c',
     '7c20c295-2615-4376-904a-c15bb5c2848c', mode(shadow_shapes), '! _ShadowView', 'Shadow View',
     'Switch the active viewport to the live Shadows view and back (right-click: options)'),
    ('6051f9dc-c82c-4c27-9089-7a1d85ef00bc', '5b0b1d51-5770-4abd-9fd9-4d9060b85fa5',
     'e1ecc95a-3121-48bb-8c33-13696ba974a6', capture(shadow_shapes), '! _ShadowCapture', 'Shadow Capture',
     'Save the raw shadows of the sun or lights in the active view as a PNG mask, hard or soft, at any size'),
    ('be91d8f5-e1b7-4e21-87a5-f2e94df3ae8e', 'fbfc6015-a813-4e89-bb8e-8e3a51aab881',
     '042dc36c-4506-4604-a7d4-03f688b88bfa', mode(ao_shapes), '! _AOView', 'AO View',
     'Switch the active viewport to the live Ambient Occlusion view and back (right-click: options)'),
    ('d3f7eb73-539e-400e-a9f5-d3c001034065', '961874cf-8c64-4660-8d7a-455a6ee9c4d1',
     '72efd309-362f-489c-8c26-007487c9a10c', capture(ao_shapes), '! _AOCapture', 'AO Capture',
     'Save an ambient occlusion pass of the active view: soft contact shadows in corners and creases'),
    ('d1e6dd72-afed-444e-87ad-0ab777d0a34f', '4243647d-146f-4fb9-aa58-9da0fccd40c9',
     'ff254c09-9a66-4e7e-8e45-d622a14fcb88', mode(normal_shapes), '! _NormalView', 'Normal View',
     'Switch the active viewport to the live Normals view and back (right-click: options)'),
    ('932c4aea-85bb-413e-bfc8-10575978a811', '7e04ec47-500d-4c45-aac1-330ddf8c0969',
     'b5016467-c77e-40da-b95d-17ccc03d85b8', capture(normal_shapes), '! _NormalCapture', 'Normal Capture',
     'Save a normal pass of the active view: PNG, 16-bit PNG or EXR, camera or world space'),
]

# Right-click on a button: (button index, macro id, script, name, tooltip)
RIGHT_MACROS = [
    (2, '490f5a7d-18e8-472f-86ed-4d1b8e3d411c', '! _ZDepthRange', 'Z Depth Range',
     'Set the depth range (auto or fixed near/far) and which end is white'),
    (4, '0b24c515-22c2-4054-80a2-81d36cc88247', '! _ImgToolsOptions', 'ImgTools Options',
     'How the Shadows, AO and Normals passes look, live and in captures'),
    (6, '2ce90562-8fcb-456a-9121-df823a473d44', '! _ImgToolsOptions', 'ImgTools Options',
     'How the Shadows, AO and Normals passes look, live and in captures'),
    (8, '2616d519-ff54-4bdb-868d-656e4d0e149f', '! _ImgToolsOptions', 'ImgTools Options',
     'How the Shadows, AO and Normals passes look, live and in captures')
]

def text(tag, value, indent):
    pad = ' ' * indent
    return f'{pad}<{tag}>\n{pad}  <locale_1033>{value}</locale_1033>\n{pad}</{tag}>\n'


def rui():
    x = '<?xml version="1.0" encoding="utf-8"?>\n'
    x += (f'<RhinoUI major_ver="5" minor_ver="0" guid="{FILE_ID}" localize="False" '
          f'default_language_id="1033" dpi_scale="100">\n')
    x += '  <extend_rhino_menus>\n'
    x += f'    <menu guid="{MENU_ID}">\n' + text('text', 'Extend Rhino Menus', 6) + '    </menu>\n'
    x += '  </extend_rhino_menus>\n'
    x += '  <menus />\n'
    x += '  <tool_bar_groups>\n'
    x += (f'    <tool_bar_group guid="{GROUP_ID}" dock_bar_guid64="{DOCK_ID}" '
          f'active_tool_bar_group="{TOOLBAR_ID}" single_file="True" '
          f'hide_single_tab="True" point_floating="400,200">\n')
    x += text('text', 'ImgTools', 6)
    x += f'      <tool_bar_group_item guid="{GROUP_ITEM_ID}" major_version="1" minor_version="1">\n'
    x += text('text', 'ImgTools', 8)
    x += f'        <tool_bar_id>{TOOLBAR_ID}</tool_bar_id>\n'
    x += '      </tool_bar_group_item>\n'
    x += ('      <dock_bar_info dpi_scale="100" dock_bar="False" docking="True" horz="True" visible="True" '
          'floating="True" mru_float_style="8192" bar_id="59650" mru_width="100" point_pos="-2,-2" '
          'float_point="400,200" dock_location_u="59419" dock_location="top" float_size="200,46" />\n')
    x += '    </tool_bar_group>\n'
    x += '  </tool_bar_groups>\n'
    x += '  <tool_bars>\n'
    x += f'    <tool_bar guid="{TOOLBAR_ID}" bitmap_id="{BUTTONS[0][2]}">\n'
    x += text('text', 'ImgTools', 6)
    right = {i: m for i, m, *_ in RIGHT_MACROS}
    for i, (item_id, macro_id, *_) in enumerate(BUTTONS):
        x += f'      <tool_bar_item guid="{item_id}">\n'
        x += f'        <left_macro_id>{macro_id}</left_macro_id>\n'
        if i in right:
            x += f'        <right_macro_id>{right[i]}</right_macro_id>\n'
        x += '      </tool_bar_item>\n'
    x += '    </tool_bar>\n'
    x += '  </tool_bars>\n'
    x += '  <macros>\n'
    for _, macro_id, bitmap_id, _, script, name, tip in BUTTONS:
        x += f'    <macro_item guid="{macro_id}" bitmap_id="{bitmap_id}">\n'
        x += text('text', name, 6) + text('tooltip', tip, 6) + text('help_text', tip, 6)
        x += text('button_text', name, 6) + text('menu_text', name, 6)
        x += f'      <script>{script}</script>\n'
        x += '    </macro_item>\n'
    for i, macro_id, script, name, tip in RIGHT_MACROS:
        x += f'    <macro_item guid="{macro_id}" bitmap_id="{BUTTONS[i][2]}">\n'
        x += text('text', name, 6) + text('tooltip', tip, 6) + text('help_text', tip, 6)
        x += text('button_text', name, 6) + text('menu_text', name, 6)
        x += f'      <script>{script}</script>\n'
        x += '    </macro_item>\n'
    x += '  </macros>\n'
    x += '  <bitmaps>\n'
    for tag, n in (('small_bitmap', 16), ('normal_bitmap', 24), ('large_bitmap', 32)):
        x += f'    <{tag} item_width="{n}" item_height="{n}">\n'
        for i, b in enumerate(BUTTONS):
            x += f'      <bitmap_item guid="{b[2]}" index="{i}" />\n'
        data = base64.b64encode(strip([b[3] for b in BUTTONS], n)).decode()
        x += f'      <bitmap>{data}</bitmap>\n'
        x += f'    </{tag}>\n'
    x += '  </bitmaps>\n'
    x += '  <scripts />\n'
    x += '</RhinoUI>\n'
    return x


def main():
    big = render(plugin_icon, 256, 4)
    with open(os.path.join(ROOT, 'dist', 'icon.png'), 'wb') as f:
        f.write(png(256, 256, big))
    sizes = [16, 24, 32, 48, 256]
    images = [(s, png(s, s, big if s == 256 else render(plugin_icon, s))) for s in sizes]
    with open(os.path.join(ROOT, 'resources', 'ImgTools.ico'), 'wb') as f:
        f.write(ico(images))
    with open(os.path.join(ROOT, 'resources', 'ImgTools.rui'), 'w', encoding='utf-8') as f:
        f.write(rui())
    print('wrote dist/icon.png, resources/ImgTools.ico, resources/ImgTools.rui')


if __name__ == '__main__':
    main()
