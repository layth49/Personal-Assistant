import time
import pyautogui
from pyautogui import ImageNotFoundException
from pynput.keyboard import Key, Controller

def navigator(text):
    while True:
        try:
            result = pyautogui.locateOnScreen(
                "..\\..\\assets\\reference.png",
                grayscale=True,
                confidence=0.8
            )
            if result is not None:
                time.sleep(1)
                break
            else:
                time.sleep(1)
        except ImageNotFoundException:
            time.sleep(1)
    try:
        keyboard = Controller()
        
        # Go to the PlayStation Library
        for x in range(11):
            keyboard.press(Key.right)
            time.sleep(0.1)
            keyboard.release(Key.right)
        
        time.sleep(0.5)
        # Gotta make sure the right arrow key is released
        keyboard.release(Key.right)
        time.sleep(1)

        # Enter the library
        keyboard.press(Key.down)
        time.sleep(0.1)
        keyboard.release(Key.down)

        time.sleep(0.3)

        # Go to the Search Button
        keyboard.press(Key.left)
        time.sleep(0.1)
        keyboard.release(Key.left)

        time.sleep(0.3)

        # Press the Search Button
        keyboard.press(Key.enter)
        time.sleep(0.1)
        keyboard.release(Key.enter)
        
        send_keystrokes(keyboard, text)

        # Finish typing
        keyboard.press(Key.enter)
        time.sleep(0.1)
        keyboard.release(Key.enter)

        time.sleep(0.3)

        # Navigate down to the Game that was searched
        keyboard.press(Key.down)
        time.sleep(0.1)
        keyboard.release(Key.down)

        # Press Enter to open the Game
        keyboard.press(Key.enter)
        time.sleep(0.1)
        keyboard.release(Key.enter)

        time.sleep(1)

        # Press the Play Button
        keyboard.press(Key.enter)
        time.sleep(0.1)
        keyboard.release(Key.enter)

    except:
        print("Error")


def send_keystrokes(keyboard, text):
  """
  This function takes text as input and sends it as keystrokes.
  """
  try:
    time.sleep(1.5)
    
    # Type in game character by character
    for char in text.lower():
       keyboard.press(char)
       time.sleep(0.1)
       keyboard.release(char)
  except:
    print("Error sending keystrokes")