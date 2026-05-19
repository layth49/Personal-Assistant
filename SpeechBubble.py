import os
os.environ["PYGAME_HIDE_SUPPORT_PROMPT"] = "1"

import ctypes

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

def _s(n):
    """Scale a logical pixel value to physical pixels for the current display."""
    return int(round(n * _DPI_SCALE))

import pygame
import win32api
import win32con
import win32gui

# Initialise pygame once at import time. Calling pygame.display.quit() +
# display.init() per bubble call destroyed and recreated SDL state, which is
# expensive on every keyword trigger. Subsystems are idempotent — reusing
# them across calls makes the bubble appear instantly.
pygame.display.init()
pygame.font.init()

_font1 = pygame.font.SysFont("Segoe UI", _s(16), bold=True)
_font2 = pygame.font.SysFont("Segoe UI", _s(20), bold=True)

RADIUS = _s(25)
BG_COLOR = (30, 31, 34)
TEXT1_COLOR = (187, 187, 187)
TEXT2_COLOR = (204, 204, 255)

PADDING_X = _s(30)
PADDING_Y = _s(20)
TEXT_SPACING = _s(1)
SPACING_BETWEEN_TEXTS = _s(20)
MAX_TEXT_WIDTH = _s(400)

ANCHOR_X = _s(750)
ANCHOR_Y = _s(750)
START_Y = _s(850)
EXIT_Y_END = _s(900)

TARGET_FPS = 60
FRAME_MS = 1000 // TARGET_FPS
ANIM_STEP_PX = _s(8)  # pixels per frame for slide-in/slide-out


def _wrap_text(text, font, max_width):
    words = text.split(" ")
    lines = []
    current = ""
    for word in words:
        candidate = f"{current} {word}" if current else word
        if font.size(candidate)[0] <= max_width:
            current = candidate
        else:
            lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def show_bubble(user_input, ai_response, state):
    import time
    t0 = time.perf_counter()
    def log(msg):
        print(f"[py t+{int((time.perf_counter()-t0)*1000)}ms] {msg}", flush=True)

    log("show_bubble: entered")

    # Fonts stay cached at module level. Display we (re)initialise per call —
    # trying to reuse a hidden SDL window across calls caused set_mode to hang
    # for ~20s before returning an invalid hwnd. Cheap to re-init since fonts
    # and other subsystems remain warm.
    if not pygame.display.get_init():
        pygame.display.init()

    font1 = _font1
    font2 = _font2

    wrapped_lines = _wrap_text(ai_response, font2, MAX_TEXT_WIDTH)

    text1 = font1.render(user_input, True, TEXT1_COLOR)
    rendered_lines = [font2.render(line, True, TEXT2_COLOR) for line in wrapped_lines]
    log("text rendered")

    text2_height = (
        sum(line.get_height() for line in rendered_lines)
        + TEXT_SPACING * max(0, len(rendered_lines) - 1)
    )
    box_width = max(
        [text1.get_width()] + [line.get_width() for line in rendered_lines]
    ) + 2 * PADDING_X
    box_height = (
        text1.get_height()
        + SPACING_BETWEEN_TEXTS
        + text2_height
        + 2 * PADDING_Y
    )

    screen = pygame.display.set_mode((box_width, box_height), pygame.NOFRAME)
    log("set_mode done")
    hwnd = pygame.display.get_wm_info()["window"]

    win32gui.SetWindowLong(
        hwnd, win32con.GWL_EXSTYLE,
        win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) | win32con.WS_EX_LAYERED,
    )
    win32gui.SetLayeredWindowAttributes(hwnd, win32api.RGB(0, 0, 0), 0, win32con.LWA_COLORKEY)

    ex_style = win32api.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
    win32api.SetWindowLong(
        hwnd, win32con.GWL_EXSTYLE,
        (ex_style | win32con.WS_EX_TOOLWINDOW) & ~win32con.WS_EX_APPWINDOW,
    )

    window_x = ANCHOR_X - box_width // 2
    target_y = ANCHOR_Y - box_height // 2
    current_y = START_Y
    win32gui.SetWindowPos(
        hwnd, win32con.HWND_TOPMOST, window_x, current_y, box_width, box_height, 0
    )

    pygame.display.set_caption("")

    clock = pygame.time.Clock()
    log("entering main loop")

    # Enter animation + idle (until state["running"] becomes False)
    while True:
        if not bool(state["running"]):
            break

        if current_y > target_y:
            current_y = max(target_y, current_y - ANIM_STEP_PX)
            win32gui.SetWindowPos(
                hwnd, win32con.HWND_TOPMOST, window_x, current_y, 0, 0,
                win32con.SWP_NOSIZE,
            )

        screen.fill((0, 0, 0))
        pygame.draw.rect(screen, BG_COLOR, (0, 0, box_width, box_height), border_radius=RADIUS)

        screen.blit(text1, (PADDING_X, PADDING_Y))
        y = PADDING_Y + text1.get_height() + SPACING_BETWEEN_TEXTS
        for line in rendered_lines:
            screen.blit(line, (PADDING_X, y))
            y += line.get_height() + TEXT_SPACING

        pygame.event.pump()
        pygame.display.update()
        clock.tick(TARGET_FPS)

    # Exit animation — slide down, frame-paced so we don't peg a core.
    while current_y < EXIT_Y_END:
        current_y = min(EXIT_Y_END, current_y + ANIM_STEP_PX)
        win32gui.SetWindowPos(
            hwnd, win32con.HWND_TOPMOST, window_x, current_y, 0, 0,
            win32con.SWP_NOSIZE,
        )
        pygame.event.pump()
        pygame.time.delay(FRAME_MS)

    # Tear the display down so the next call gets a fresh window. Trying to
    # reuse a hidden SDL window between calls caused set_mode to hang for
    # ~20s and return a stale hwnd.
    pygame.display.quit()
