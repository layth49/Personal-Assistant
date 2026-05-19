import os
import time
import traceback
import pyautogui
from pyautogui import ImageNotFoundException


# Walk up the directory tree from __file__ until assets\ is found. This works
# whether the script is at the worktree root or copied into bin\Release\.
def _find_assets_dir(start):
    current = os.path.dirname(os.path.abspath(start))
    for _ in range(5):
        candidate = os.path.join(current, "assets")
        if os.path.isdir(candidate):
            return candidate
        current = os.path.dirname(current)
    return None

_ASSETS_DIR = _find_assets_dir(__file__)
COMPOSE_IMAGE = os.path.join(_ASSETS_DIR, "compose.png") if _ASSETS_DIR else "assets/compose.png"
LOCATE_TIMEOUT_SECONDS = 30


def _log(msg):
    print(f"[sms] {msg}", flush=True)


def _find_compose_button(timeout=LOCATE_TIMEOUT_SECONDS):
    _log(f"looking for compose button at {COMPOSE_IMAGE}")
    _log(f"image exists on disk: {os.path.isfile(COMPOSE_IMAGE)}")
    _log(f"screen size: {pyautogui.size()}")

    deadline = time.time() + timeout
    attempts = 0
    while time.time() < deadline:
        attempts += 1
        try:
            button = pyautogui.locateOnScreen(COMPOSE_IMAGE, grayscale=True, confidence=0.7)
            if button is not None:
                _log(f"found compose button after {attempts} attempt(s) at {button}")
                return button
            else:
                _log(f"attempt {attempts}: locateOnScreen returned None")
        except ImageNotFoundException:
            _log(f"attempt {attempts}: ImageNotFoundException")
        except Exception as e:
            _log(f"attempt {attempts}: unexpected error: {type(e).__name__}: {e}")
        time.sleep(1)
    _log(f"compose button not found after {attempts} attempts ({timeout}s)")
    return None


def smsService(contactNumber, message_body):
    """Open a new message in Phone Link and send `message_body` to `contactNumber`.

    Returns True on apparent success, False if the compose button can't be found.
    """
    _log(f"smsService called with contactNumber={contactNumber!r}, message_body={message_body!r}")
    _log(f"active window title (current foreground): {_get_foreground_window_title()}")

    button = _find_compose_button()
    if button is None:
        _log(f"BAILING OUT: compose button not found at {COMPOSE_IMAGE}")
        return False

    try:
        center = pyautogui.center(button)
        _log(f"clicking compose button at {center}")
        pyautogui.click(button)
        time.sleep(1)
        _log(f"after compose click, foreground window: {_get_foreground_window_title()}")

        _log(f"typing contact number: {contactNumber!r}")
        pyautogui.write(contactNumber)
        time.sleep(0.1)
        _log("pressing enter to confirm recipient")
        pyautogui.press("enter")
        time.sleep(1)
        _log(f"after recipient enter, foreground window: {_get_foreground_window_title()}")

        _log(f"typing message body ({len(message_body)} chars): {message_body!r}")
        pyautogui.write(message_body)
        time.sleep(0.1)
        _log("pressing enter to send")
        pyautogui.press("enter")
        _log("smsService: completed successfully")
        return True
    except Exception:
        _log("smsService: exception during send sequence:")
        traceback.print_exc()
        return False


def _get_foreground_window_title():
    """Best-effort foreground window title for debugging which app is focused."""
    try:
        import win32gui
        hwnd = win32gui.GetForegroundWindow()
        return f"{win32gui.GetWindowText(hwnd)!r} (hwnd={hwnd})"
    except Exception as e:
        return f"<error reading foreground window: {e}>"
