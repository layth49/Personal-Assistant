import os
os.environ["PYGAME_HIDE_SUPPORT_PROMPT"] = "1"
import pygame
import numpy as np
import sounddevice as sd
import win32api
import win32con
import win32gui

#-
# Configuration
SAMPLE_RATE = 44100
FFT_SIZE = 8192
NUM_BARS = 70
BAR_COLOR = (255, 255, 255)
RECT_COLOR = (30, 30, 30)
SCALE_FACTOR = 0.5  # Adjust this for smaller or larger visualization
PADDING = 10  # Padding between the bars and the edges of the rectangle

# Adjust dimensions based on scale
WIDTH, HEIGHT = int(600 * SCALE_FACTOR), int(550 * SCALE_FACTOR)
RECT_WIDTH = int(500 * SCALE_FACTOR)
RECT_HEIGHT = int(150 * SCALE_FACTOR)
RECT_RADIUS = int(30 * SCALE_FACTOR)

FREQ_MIN = 20
FREQ_MAX = 24000
DB_MIN = -20
DB_MAX = 50
SMOOTHING_FACTOR = 0.9

smoothed_amplitudes = np.zeros(NUM_BARS, dtype=np.float32)
audio_buffer = np.zeros(FFT_SIZE, dtype=np.float32)

def audio_callback(indata, frames, time, status):
    """Callback function to process audio data in real-time."""
    global audio_buffer
    if status:
        print(status)
    audio_buffer = indata[:, 0]

def get_log_bins(fft_data, num_bars, sample_rate, freq_min, freq_max, db_min, db_max):
    """Map FFT results to logarithmic bins with decibel normalization."""
    fft_size = len(fft_data) * 2
    log_bins = np.geomspace(freq_min, freq_max, num=num_bars + 1)
    bin_indices = (log_bins / (sample_rate / fft_size)).astype(int)
    bin_indices = np.clip(bin_indices, 0, len(fft_data) - 1)

    bar_amplitudes = []
    for i in range(len(bin_indices) - 1):
        start, end = bin_indices[i], bin_indices[i + 1]
        if start >= end:
            bar_amplitudes.append(0)
        else:
            amplitude = np.max(fft_data[start:end])  # Changed from mean to max
            decibel = 20 * np.log10(amplitude + 1e-8)
            decibel = np.clip(decibel, db_min, db_max)
            normalized = (decibel - db_min) / (db_max - db_min)
            # Add exponential scaling for more dramatic effect
            normalized = np.power(normalized, 0.8)  
            bar_amplitudes.append(normalized)

    return np.array(bar_amplitudes)

def draw_rounded_rect(surface, color, rect, radius):
    """Draw a rounded rectangle with a white outline."""
    # Draw white outline (slightly larger rectangle)
    outline_rect = (rect[0]-1, rect[1]-1, rect[2]+2, rect[3]+2)
    pygame.draw.rect(surface, (255, 255, 255), outline_rect, border_radius=radius)
    
    # Draw inner rectangle with original color
    pygame.draw.rect(surface, color, rect, border_radius=radius)

def animate_close(hwnd, start_y, target_y):#+
    current_y = start_y#+
    while current_y < target_y:#+
        current_y += 1  # Speed of descending#+
        win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, 600, current_y, 0, 0, win32con.SWP_NOSIZE)#+
        pygame.time.wait(10)  # Add a small delay to make the animation visible#+
def main():
    # Initialize pygame
    pygame.init()
    screen = pygame.display.set_mode((WIDTH, HEIGHT), pygame.NOFRAME)
    hwnd = pygame.display.get_wm_info()["window"]

    win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) | win32con.WS_EX_LAYERED)
    win32gui.SetLayeredWindowAttributes(hwnd, win32api.RGB(0, 0, 0), 0, win32con.LWA_COLORKEY)

    ex_style = win32api.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
    win32api.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, (ex_style | win32con.WS_EX_TOOLWINDOW) & ~win32con.WS_EX_APPWINDOW)

    # Starting position for animation
    target_y = 600
    current_y = 750  # Start from below the screen
    win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, 600, current_y, 0, 0, win32con.SWP_NOSIZE)

    stream = sd.InputStream(samplerate=SAMPLE_RATE, channels=1, callback=audio_callback)
    stream.start()

    running = True
    closing = False#+
    while running:
        for event in pygame.event.get():
            if event.type == pygame.QUIT:
                running = False#-
                closing = True#+

        # Animate the rectangle rising#-
        if current_y > target_y:#-
            current_y -= 1  # Speed of rising#-
            win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, 600, current_y, 0, 0, win32con.SWP_NOSIZE)#-
        if closing:#+
            animate_close(hwnd, current_y, 750)  # Animate down to the starting position#+
            running = False#+
        else:#+
            # Animate the rectangle rising#+
            if current_y > target_y:#+
                current_y -= 1  # Speed of rising#+
                win32gui.SetWindowPos(hwnd, win32con.HWND_TOPMOST, 600, current_y, 0, 0, win32con.SWP_NOSIZE)#+

        fft_result = np.abs(np.fft.rfft(audio_buffer, n=FFT_SIZE))
        fft_result = np.nan_to_num(fft_result, nan=0.0, posinf=0.0, neginf=0.0)

        log_amplitudes = get_log_bins(
            fft_result, NUM_BARS, SAMPLE_RATE, FREQ_MIN, FREQ_MAX, DB_MIN, DB_MAX
        )

        global smoothed_amplitudes
        smoothed_amplitudes = (
            SMOOTHING_FACTOR * smoothed_amplitudes
            + (1 - SMOOTHING_FACTOR) * log_amplitudes
        )

        screen.fill((0, 0, 0, 0))  # Transparent background

        rect_x = (WIDTH - RECT_WIDTH) // 2
        rect_y = (HEIGHT - RECT_HEIGHT) // 2
        draw_rounded_rect(screen, RECT_COLOR, (rect_x, rect_y, RECT_WIDTH, RECT_HEIGHT), RECT_RADIUS)

        bar_width = (RECT_WIDTH - 2 * PADDING) // NUM_BARS
        center_y = rect_y + RECT_HEIGHT // 2
        for i, amplitude in enumerate(smoothed_amplitudes):
            bar_height = int(amplitude * (RECT_HEIGHT // 2))
            x = rect_x + PADDING + i * bar_width
            y_top = center_y - bar_height
            y_bottom = center_y
            pygame.draw.rect(screen, BAR_COLOR, (x, y_top, bar_width - 2, bar_height))
            pygame.draw.rect(screen, BAR_COLOR, (x, y_bottom, bar_width - 2, bar_height))

        pygame.display.flip()

    stream.stop()
    pygame.quit()

if __name__ == "__main__":
    main()
