import os 
import pyautogui
import json
import time
import azure.cognitiveservices.speech as speechsdk


def smsService(contactNumber, message_body):
    # Press the compose button
    while True:
        try:
            button = pyautogui.locateOnScreen("..\\..\\assets\\compose.png", grayscale= True, confidence= 0.8)
            pyautogui.click(button)

            time.sleep(1)
            break
        except:
            time.sleep(1)

    # Type the recipient's number
    pyautogui.write(contactNumber)
    time.sleep(0.1)
    pyautogui.press("enter")

    time.sleep(1)

    # Type the message
    pyautogui.write(message_body)
    time.sleep(0.1)
    pyautogui.press("enter")