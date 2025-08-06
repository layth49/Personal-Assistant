import os
os.environ["PYGAME_HIDE_SUPPORT_PROMPT"] = "1"
import pygame
import win32api
import win32con
import win32gui


# Setup
pygame.init()
RADIUS = 25

# Fonts
pygame.font.init()
font1 = pygame.font.SysFont("Segoe UI", 16, bold=True)
font2 = pygame.font.SysFont("Segoe UI", 20, bold=True)

# Colors
BG_COLOR = (30, 31, 34)  # RGBA with alpha
TEXT1_COLOR = (187, 187, 187)
TEXT2_COLOR = (204, 204, 255)

# Running flag
state = {"running": True}

def show_bubble(user_input, ai_response, state):
    # Text content
    speaker_question = user_input
    AI_response = ai_response

    # Padding and spacing
    padding_x = 30
    padding_y = 20
    text_spacing = 1 
    spacing_between_text1_and_text2 = 20  
    max_text_width = 400  # Max width for wrapping

    # Function to wrap text manually
    def wrap_text(text, font, max_width):
        words = text.split(' ')
        lines = []
        current_line = ""

        for word in words:
            test_line = f"{current_line} {word}" if current_line else word
            if font.size(test_line)[0] <= max_width:
                current_line = test_line
            else:
                lines.append(current_line)
                current_line = word

        if current_line:
            lines.append(current_line)

        return lines

    wrapped_text2_lines = wrap_text(AI_response, font2, max_text_width)

    # Render text
    text1 = font1.render(speaker_question, True, TEXT1_COLOR)
    rendered_text2 = [font2.render(line, True, TEXT2_COLOR) for line in wrapped_text2_lines]

    # Calculate box size based on text
    text2_height = sum(line.get_height() for line in rendered_text2) + text_spacing * (len(rendered_text2) - 1)
    box_width = max([text1.get_width()] + [line.get_width() for line in rendered_text2]) + 2 * padding_x
    box_height = text1.get_height() + spacing_between_text1_and_text2 + text2_height + 2 * padding_y

    # Create transparent display surface with NOFRAME
    screen = pygame.display.set_mode((box_width, box_height), pygame.NOFRAME)
    hwnd = pygame.display.get_wm_info()["window"]

    # Make window transparent and always on top
    win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) | win32con.WS_EX_LAYERED)
    win32gui.SetLayeredWindowAttributes(hwnd, win32api.RGB(0, 0, 0), 0, win32con.LWA_COLORKEY)
    ex_style = win32api.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
    win32api.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, (ex_style | win32con.WS_EX_TOOLWINDOW) & ~win32con.WS_EX_APPWINDOW)

    window_x = 750 - box_width // 2
    target_window_y = 750 - box_height // 2
    current_y = 850
    win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, window_x, current_y, box_width, box_height, 0)

    pygame.display.set_caption("")

    # Function to simplify drawing a rounded rectangle
    def draw_rounded_rect(surface, color, rect, radius):
        pygame.draw.rect(surface, color, rect, border_radius=radius)

    # Main loop
    while True:
        running_flag = bool(state["running"])  # force correct Python truth evaluation

        # Animate the rectangle falling and break the loop if not running
        if not running_flag:
            while current_y <= 900:
                current_y += 1  # Speed of falling
                win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, window_x, current_y, 0, 0, win32con.SWP_NOSIZE)
            break

        # Animate the rectangle rising
        if current_y >= target_window_y:
            current_y -= 1  # Speed of rising
            win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, window_x, current_y, 0, 0, win32con.SWP_NOSIZE)
        screen.fill((0, 0, 0, 0))

        # Draw box
        draw_rounded_rect(screen, BG_COLOR, (0, 0, box_width, box_height), RADIUS)

        # Draw text
        screen.blit(text1, (padding_x, padding_y))
        y_offset = padding_y + text1.get_height() + spacing_between_text1_and_text2
        for line in rendered_text2:
            screen.blit(line, (padding_x, y_offset))
            y_offset += line.get_height() + text_spacing

        pygame.event.pump()
        pygame.display.update()

        if (current_y < target_window_y):
            pygame.time.delay(1000)

    pygame.display.quit()