import os
import time
import traceback
import pyautogui
from pyautogui import ImageNotFoundException
from pynput.keyboard import Key, Controller


# AutoRemotePlay.py is copied to bin\Release\ by the .csproj, so __file__
# resolves to bin\Release\ — not the worktree root where assets\ lives.
# Walk up the directory tree until assets\ is found (max 5 levels) so the
# path resolves correctly regardless of where the script is placed.
def _find_assets_dir(start):
    current = os.path.dirname(os.path.abspath(start))
    for _ in range(5):
        candidate = os.path.join(current, "assets")
        if os.path.isdir(candidate):
            return candidate
        current = os.path.dirname(current)
    return None

_ASSETS_DIR = _find_assets_dir(__file__)
REFERENCE_IMAGE = os.path.join(_ASSETS_DIR, "reference.png") if _ASSETS_DIR else "assets/reference.png"
LOCATE_TIMEOUT_SECONDS = 60
KEY_TAP_DELAY = 0.1
SHORT_PAUSE = 0.3
MEDIUM_PAUSE = 1.0
LONG_PAUSE = 2


def _log(msg):
    print(f"[arplay] {msg}", flush=True)


def _wait_for_reference(timeout=LOCATE_TIMEOUT_SECONDS):
    """Block until the reference image appears on screen, or timeout elapses."""
    _log(f"assets dir: {_ASSETS_DIR}")
    _log(f"waiting for reference image at {REFERENCE_IMAGE}")
    _log(f"image exists on disk: {os.path.isfile(REFERENCE_IMAGE)}")
    _log(f"screen size: {pyautogui.size()}")

    deadline = time.time() + timeout
    attempts = 0
    while time.time() < deadline:
        attempts += 1
        try:
            result = pyautogui.locateOnScreen(REFERENCE_IMAGE, grayscale=True, confidence=0.8)
            if result is not None:
                _log(f"reference image found after {attempts} attempt(s) at {result}")
                time.sleep(MEDIUM_PAUSE)
                return True
            else:
                _log(f"attempt {attempts}: locateOnScreen returned None")
        except ImageNotFoundException:
            _log(f"attempt {attempts}: ImageNotFoundException")
        except Exception as e:
            _log(f"attempt {attempts}: unexpected error: {type(e).__name__}: {e}")
        time.sleep(1)

    _log(f"timed out after {attempts} attempts ({timeout}s) — reference image never found")
    return False


def _tap(keyboard, key, hold=KEY_TAP_DELAY):
    keyboard.press(key)
    time.sleep(hold)
    keyboard.release(key)


def navigator(text):
    _log(f"navigator called with text={text!r}")
    _log(f"active window before wait: {_get_foreground_window_title()}")

    if not _wait_for_reference():
        _log(f"BAILING OUT: timed out waiting for reference image at {REFERENCE_IMAGE}")
        return

    _log(f"active window after reference found: {_get_foreground_window_title()}")

    try:
        keyboard = Controller()

        # Move to the PlayStation Library
        _log("pressing right x11 to reach library")
        for _ in range(11):
            _tap(keyboard, Key.right)

        time.sleep(0.5)
        keyboard.release(Key.right)  # belt-and-suspenders
        time.sleep(LONG_PAUSE)

        # Enter the library
        _log("pressing down to enter library")
        _tap(keyboard, Key.down)
        time.sleep(SHORT_PAUSE)

        # Go to the Search button
        _log("pressing left to reach search button")
        _tap(keyboard, Key.left)
        time.sleep(SHORT_PAUSE)

        # Open the search field
        _log("pressing enter to open search field")
        _tap(keyboard, Key.enter)

        _log(f"typing game name: {text!r}")
        _send_keystrokes(keyboard, text)

        # Submit search
        _log("pressing enter to submit search")
        _tap(keyboard, Key.enter)
        time.sleep(SHORT_PAUSE)

        # Move down to the result
        _log("pressing down to select first result")
        _tap(keyboard, Key.down)

        # Open the game
        _log("pressing enter to open game")
        _tap(keyboard, Key.enter)
        time.sleep(LONG_PAUSE)

        # Hit Play
        _log("pressing enter to hit Play")
        _tap(keyboard, Key.enter)

        _log("navigator: completed successfully")

    except Exception:
        _log("navigator: exception during navigation sequence:")
        traceback.print_exc()


def _send_keystrokes(keyboard, text):
    """Send text as keystrokes, one character at a time."""
    _log(f"_send_keystrokes: typing {len(text)} characters")
    try:
        time.sleep(1.5)
        for i, char in enumerate(text.lower()):
            _log(f"  char {i+1}/{len(text)}: {char!r}")
            _tap(keyboard, char)
        _log("_send_keystrokes: done")
    except Exception:
        _log("_send_keystrokes: exception:")
        traceback.print_exc()


def _get_foreground_window_title():
    """Best-effort foreground window title for debugging."""
    try:
        import win32gui
        hwnd = win32gui.GetForegroundWindow()
        return f"{win32gui.GetWindowText(hwnd)!r} (hwnd={hwnd})"
    except Exception as e:
        return f"<error: {e}>"


# Public name kept for backward compatibility with the C# caller.
send_keystrokes = _send_keystrokes
