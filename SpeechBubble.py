import os
os.environ["PYGAME_HIDE_SUPPORT_PROMPT"] = "1"

import ctypes
from ctypes import wintypes

# Set DPI awareness BEFORE pygame imports. PyAutoGUI (used by SMSService and
# AutoRemotePlay) calls SetProcessDPIAware() at its import time. If that runs
# AFTER pygame initialises in non-DPI-aware mode, the SDL window switches from
# logical to physical pixels mid-run — the bubble visibly shrinks (to ~1/4
# area on a 200% scale display) and SetWindowPos coordinates jump. By making
# the process DPI-aware up front, pygame initialises in DPI-aware mode and
# pyautogui's later call is a no-op, so behaviour stays consistent.
def _set_dpi_awareness():
    try:
        # PROCESS_PER_MONITOR_DPI_AWARE = 2 (Win 8.1+, recommended)
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        return
    except (AttributeError, OSError):
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except (AttributeError, OSError):
        pass

_set_dpi_awareness()

def _get_dpi_scale():
    """Returns the primary monitor's DPI scale factor (1.0 == 100%, 2.0 == 200%)."""
    try:
        hdc = ctypes.windll.user32.GetDC(0)
        try:
            dpi = ctypes.windll.gdi32.GetDeviceCaps(hdc, 88)  # LOGPIXELSX
        finally:
            ctypes.windll.user32.ReleaseDC(0, hdc)
        return dpi / 96.0 if dpi else 1.0
    except Exception:
        return 1.0

_DPI_SCALE = _get_dpi_scale()

# We render everything at SS× the target resolution and downsample once with a
# Lanczos filter. PIL's rounded_rectangle and ellipse do NOT anti-alias, so
# supersampling is what gives us smooth bubble corners and a clean logo.
_SS = 2

def _s(n):
    """Scale a logical pixel value to physical pixels for the current display."""
    return int(round(n * _DPI_SCALE))

def _r(n):
    """Scale a logical pixel value to supersampled render-space pixels."""
    return int(round(n * _DPI_SCALE * _SS))

import time
import pygame
import win32con
import win32gui
from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageFilter

# Initialise pygame's display subsystem once. We only use it to host a native
# window (for its HWND + event pump); all pixels are drawn with PIL and pushed
# to the window via UpdateLayeredWindow, so no SDL rendering happens.
pygame.display.init()

# ── Appearance ──────────────────────────────────────────────────────────────
BG_COLOR = (30, 31, 34, 214)          # translucent "glass" panel
BORDER_COLOR = (255, 255, 255, 26)    # hairline top-light border
SHADOW_COLOR = (0, 0, 0, 150)         # soft drop shadow
USER_COLOR = (154, 154, 162, 255)     # muted grey for the echoed prompt
REPLY_COLOR = (204, 204, 255, 255)    # lavender for the assistant reply
LOGO_TINT = (150, 133, 255)           # saturated lavender-purple, clearly tinted

RADIUS = _r(22)
PADDING_X = _r(24)
PADDING_Y = _r(16)
USER_REPLY_GAP = _r(10)               # gap between the prompt line and the reply
LINE_LEADING = _r(3)                  # extra spacing between wrapped reply lines
MAX_TEXT_WIDTH = _r(400)

LOGO_HEIGHT = _r(30)
LOGO_GAP = _r(16)                     # logo → text

SHADOW_PAD = _r(46)                   # transparent margin around the bubble for the shadow
SHADOW_OFFSET_Y = _r(9)
SHADOW_BLUR = _r(15)

# ── Behaviour ───────────────────────────────────────────────────────────────
MARGIN_BOTTOM = _s(60)                # gap from the work-area bottom to the bubble
SLIDE_DISTANCE = _s(55)               # how far the bubble travels on enter/exit
ENTER_DURATION = 0.28                 # seconds
EXIT_DURATION = 0.22


def _load_logo():
    here = os.path.dirname(os.path.abspath(__file__))
    for candidate in (
        os.path.join(here, "logo.ico"),
        os.path.join(here, "assets", "logo.ico"),
        "logo.ico",
    ):
        try:
            if os.path.exists(candidate):
                return Image.open(candidate).convert("RGBA")
        except Exception:
            continue
    return None


def _tint(img, rgb):
    """Recolour an RGBA image to a flat colour, preserving its alpha shape."""
    solid = Image.new("RGBA", img.size, rgb + (255,))
    solid.putalpha(img.split()[3])
    return solid


_LOGO = _load_logo()
_LOGO_TINTED = _tint(_LOGO, LOGO_TINT) if _LOGO is not None else None


def _font(size, bold=True):
    name = "segoeuib.ttf" if bold else "segoeui.ttf"
    for path in (os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", name), name):
        try:
            return ImageFont.truetype(path, size)
        except Exception:
            continue
    return ImageFont.load_default()


_FONT_USER = _font(_r(16))
_FONT_REPLY = _font(_r(20))


def _wrap_text(text, font, max_width):
    words = text.split(" ")
    lines = []
    current = ""
    for word in words:
        candidate = f"{current} {word}" if current else word
        if font.getlength(candidate) <= max_width:
            current = candidate
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines or [""]


# ── Win32 plumbing for a per-pixel-alpha layered window ──────────────────────
_user32 = ctypes.windll.user32
_gdi32 = ctypes.windll.gdi32


class _POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class _SIZE(ctypes.Structure):
    _fields_ = [("cx", ctypes.c_long), ("cy", ctypes.c_long)]


class _RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class _BLENDFUNCTION(ctypes.Structure):
    _fields_ = [("BlendOp", ctypes.c_byte), ("BlendFlags", ctypes.c_byte),
                ("SourceConstantAlpha", ctypes.c_byte), ("AlphaFormat", ctypes.c_byte)]


class _BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class _BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", _BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


class _MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", _RECT),
                ("rcWork", _RECT), ("dwFlags", wintypes.DWORD)]


# Explicit prototypes — without argtypes ctypes coerces handle args to c_int,
# which overflows on 64-bit pointers ("int too long to convert").
_user32.MonitorFromPoint.argtypes = [_POINT, wintypes.DWORD]
_user32.MonitorFromPoint.restype = wintypes.HANDLE
_user32.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_MONITORINFO)]
_user32.GetCursorPos.argtypes = [ctypes.POINTER(_POINT)]
_user32.GetDC.argtypes = [wintypes.HWND]
_user32.GetDC.restype = wintypes.HDC
_user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
_user32.UpdateLayeredWindow.argtypes = [
    wintypes.HWND, wintypes.HDC, ctypes.POINTER(_POINT), ctypes.POINTER(_SIZE),
    wintypes.HDC, ctypes.POINTER(_POINT), wintypes.DWORD,
    ctypes.POINTER(_BLENDFUNCTION), wintypes.DWORD,
]
_user32.UpdateLayeredWindow.restype = wintypes.BOOL
_gdi32.CreateDIBSection.argtypes = [
    wintypes.HDC, ctypes.c_void_p, wintypes.UINT,
    ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD,
]
_gdi32.CreateDIBSection.restype = wintypes.HBITMAP
_gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
_gdi32.CreateCompatibleDC.restype = wintypes.HDC
_gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HANDLE]
_gdi32.SelectObject.restype = wintypes.HANDLE
_gdi32.DeleteObject.argtypes = [wintypes.HANDLE]
_gdi32.DeleteDC.argtypes = [wintypes.HDC]

_ULW_ALPHA = 0x02
_AC_SRC_OVER = 0x00
_AC_SRC_ALPHA = 0x01
_MONITOR_DEFAULTTONEAREST = 0x02


def _active_work_area():
    """Work area (screen minus taskbar) of the monitor under the cursor."""
    pt = _POINT(0, 0)
    _user32.GetCursorPos(ctypes.byref(pt))
    hmon = _user32.MonitorFromPoint(pt, _MONITOR_DEFAULTTONEAREST)
    mi = _MONITORINFO()
    mi.cbSize = ctypes.sizeof(_MONITORINFO)
    _user32.GetMonitorInfoW(hmon, ctypes.byref(mi))
    return mi.rcWork


def _image_to_dib(img):
    """Build a top-down 32bpp DIB with premultiplied BGRA from a PIL RGBA image.

    UpdateLayeredWindow with AC_SRC_ALPHA expects premultiplied alpha, so each
    colour channel is scaled by the alpha before we hand the bytes to GDI.
    """
    r, g, b, a = img.split()
    premult = Image.merge("RGBA", (
        ImageChops.multiply(r, a),
        ImageChops.multiply(g, a),
        ImageChops.multiply(b, a),
        a,
    ))
    data = premult.tobytes("raw", "BGRA")
    w, h = img.size

    hdc_screen = _user32.GetDC(0)
    bmi = _BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(_BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h  # negative → top-down, matching PIL's row order
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0  # BI_RGB
    bits = ctypes.c_void_p()
    hbmp = _gdi32.CreateDIBSection(hdc_screen, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    ctypes.memmove(bits, data, len(data))

    memdc = _gdi32.CreateCompatibleDC(hdc_screen)
    old = _gdi32.SelectObject(memdc, hbmp)
    return {"hdc_screen": hdc_screen, "memdc": memdc, "hbmp": hbmp,
            "old": old, "size": _SIZE(w, h)}


def _push(hwnd, dib, x, y, alpha):
    """Position the window at (x, y) and present the DIB at the given opacity."""
    pt_dst = _POINT(int(x), int(y))
    pt_src = _POINT(0, 0)
    blend = _BLENDFUNCTION(_AC_SRC_OVER, 0, int(max(0, min(255, alpha))), _AC_SRC_ALPHA)
    _user32.UpdateLayeredWindow(
        hwnd, dib["hdc_screen"], ctypes.byref(pt_dst), ctypes.byref(dib["size"]),
        dib["memdc"], ctypes.byref(pt_src), 0, ctypes.byref(blend), _ULW_ALPHA,
    )


def _free_dib(dib):
    _gdi32.SelectObject(dib["memdc"], dib["old"])
    _gdi32.DeleteObject(dib["hbmp"])
    _gdi32.DeleteDC(dib["memdc"])
    _user32.ReleaseDC(0, dib["hdc_screen"])


def _build_image(user_input, ai_response):
    """Compose the whole bubble (shadow + glass panel + accent + logo + text)
    into a single supersampled RGBA image, then downsample for smooth edges.
    Returns (image, box_left, box_top, box_w, box_h) in physical pixels, where
    the box_* values locate the visible panel inside the padded image."""
    user_input = (user_input or "").strip()
    has_user = bool(user_input)

    lines = _wrap_text(ai_response, _FONT_REPLY, MAX_TEXT_WIDTH)

    u_asc, u_desc = _FONT_USER.getmetrics()
    r_asc, r_desc = _FONT_REPLY.getmetrics()
    user_h = u_asc + u_desc
    reply_line_h = r_asc + r_desc
    reply_h = len(lines) * reply_line_h + (len(lines) - 1) * LINE_LEADING

    text_w = max([_FONT_REPLY.getlength(l) for l in lines]
                 + ([_FONT_USER.getlength(user_input)] if has_user else []))
    text_w = int(text_w)
    text_h = reply_h + (user_h + USER_REPLY_GAP if has_user else 0)

    logo_w = logo_h = 0
    logo_img = None
    if _LOGO_TINTED is not None:
        logo_h = LOGO_HEIGHT
        logo_w = int(logo_h * _LOGO_TINTED.width / _LOGO_TINTED.height)
        logo_img = _LOGO_TINTED.resize((logo_w, logo_h), Image.LANCZOS)

    content_h = max(text_h, logo_h)
    content_w = logo_w + LOGO_GAP + text_w
    box_w = content_w + 2 * PADDING_X
    box_h = content_h + 2 * PADDING_Y

    img_w = box_w + 2 * SHADOW_PAD
    img_h = box_h + 2 * SHADOW_PAD
    img = Image.new("RGBA", (img_w, img_h), (0, 0, 0, 0))

    bx, by = SHADOW_PAD, SHADOW_PAD  # top-left of the panel

    # Drop shadow: a blurred, offset rounded rect on its own layer.
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [bx, by + SHADOW_OFFSET_Y, bx + box_w, by + SHADOW_OFFSET_Y + box_h],
        radius=RADIUS, fill=SHADOW_COLOR,
    )
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(SHADOW_BLUR)))

    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle([bx, by, bx + box_w - 1, by + box_h - 1],
                           radius=RADIUS, fill=BG_COLOR)
    draw.rounded_rectangle([bx, by, bx + box_w - 1, by + box_h - 1],
                           radius=RADIUS, outline=BORDER_COLOR, width=max(1, _r(1)))

    inner_x = bx + PADDING_X
    inner_y = by + PADDING_Y

    x = inner_x
    if logo_img is not None:
        img.alpha_composite(logo_img, (x, inner_y + (content_h - logo_h) // 2))
        x += logo_w + LOGO_GAP

    ty = inner_y + (content_h - text_h) // 2
    if has_user:
        draw.text((x, ty), user_input, font=_FONT_USER, fill=USER_COLOR)
        ty += user_h + USER_REPLY_GAP
    for line in lines:
        draw.text((x, ty), line, font=_FONT_REPLY, fill=REPLY_COLOR)
        ty += reply_line_h + LINE_LEADING

    # Downsample once for anti-aliased corners, logo, and text.
    out = img.resize((round(img_w / _SS), round(img_h / _SS)), Image.LANCZOS)
    scale = out.width / img_w
    return (out,
            int(round(bx * scale)), int(round(by * scale)),
            int(round(box_w * scale)), int(round(box_h * scale)))


def _ease_out(t):
    return 1 - (1 - t) ** 3


def _ease_in(t):
    return t * t * t


def show_bubble(user_input, ai_response, state):
    t0 = time.perf_counter()
    def log(msg):
        print(f"[py t+{int((time.perf_counter()-t0)*1000)}ms] {msg}", flush=True)

    log("show_bubble: entered")

    if not pygame.display.get_init():
        pygame.display.init()

    img, box_left, box_top, box_w, box_h = _build_image(user_input, ai_response)
    img_w, img_h = img.size
    log("image composed")

    # Create the native window off-screen so SDL's initial frame is never seen;
    # the layered content we push is what actually becomes visible.
    os.environ["SDL_VIDEO_WINDOW_POS"] = "-10000,-10000"
    pygame.display.set_mode((img_w, img_h), pygame.NOFRAME)
    hwnd = pygame.display.get_wm_info()["window"]
    pygame.display.set_caption("")
    log("window created")

    # Per-pixel-alpha layered window, hidden from taskbar/alt-tab.
    ex = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
    ex = (ex | win32con.WS_EX_LAYERED | win32con.WS_EX_TOOLWINDOW) & ~win32con.WS_EX_APPWINDOW
    win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, ex)

    work = _active_work_area()
    window_x = work.left + ((work.right - work.left) - img_w) // 2
    # Align the *visible panel* (not the padded image) a fixed margin above the taskbar.
    target_y = work.bottom - MARGIN_BOTTOM - (box_top + box_h)
    start_y = target_y + SLIDE_DISTANCE

    dib = _image_to_dib(img)
    _push(hwnd, dib, window_x, start_y, 0)
    win32gui.SetWindowPos(
        hwnd, win32con.HWND_TOPMOST, window_x, start_y, img_w, img_h,
        win32con.SWP_NOACTIVATE,
    )
    log("entering main loop")

    try:
        # Enter: slide up + fade in (ease-out).
        anim_t0 = time.perf_counter()
        while True:
            t = min(1.0, (time.perf_counter() - anim_t0) / ENTER_DURATION)
            p = _ease_out(t)
            y = start_y + (target_y - start_y) * p
            _push(hwnd, dib, window_x, y, int(255 * p))
            pygame.event.pump()
            if t >= 1.0:
                break
            time.sleep(0.006)

        # Idle: hold in place until C# flips state["running"] to False.
        while bool(state["running"]):
            pygame.event.pump()
            time.sleep(0.02)

        # Exit: slide down + fade out (ease-in).
        anim_t0 = time.perf_counter()
        while True:
            t = min(1.0, (time.perf_counter() - anim_t0) / EXIT_DURATION)
            p = _ease_in(t)
            y = target_y + SLIDE_DISTANCE * p
            _push(hwnd, dib, window_x, y, int(255 * (1 - p)))
            pygame.event.pump()
            if t >= 1.0:
                break
            time.sleep(0.006)
    finally:
        _free_dib(dib)
        # Tear the window down so the next call gets a fresh one. Reusing a
        # hidden SDL window between calls caused set_mode to hang for ~20s and
        # return a stale hwnd.
        pygame.display.quit()