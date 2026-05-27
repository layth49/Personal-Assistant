# L.A.I.T.H.49 — Personal Assistant

Voice assistant for Windows, .NET Framework 4.8.1, AnyCPU. This branch (`local-laith`) runs the AI stack entirely on the host machine.

## What the program does

1. Idles on a wake-word (`Hey 49`) detected on-device.
2. Plays a time-of-day greeting and listens for a command.
3. Either executes a built-in command (lights, weather, prayer times, SMS, PlayStation, doors, shutdown, YouTube, etc.) or falls back to the local LLM for a free-form answer.

## Local stack — what replaces what

| Subsystem | Cloud (`main`) | Local (this branch) | Default endpoint |
|---|---|---|---|
| Wake word | Azure `KeywordRecognizer` (on-device) | **unchanged** — runs against `keyword.table` | n/a |
| LLM | Gemini 2.5 Flash + Google-search grounding | LM Studio (Llama 3.1 8B Q4_K_M) + SearxNG | `http://localhost:1234/v1`, `http://localhost:8080` |
| STT | Azure Speech-to-Text | faster-whisper-server (Whisper large-v3) | `http://localhost:8000` |
| TTS | Azure Neural TTS (`en-US-AndrewMultilingualNeural`) | Kokoro-FastAPI | `http://localhost:8880` |
| Mic capture (for STT) | Azure SDK | NAudio `WaveInEvent`, 16 kHz mono PCM | n/a |
| Audio output | Azure SDK internal player | NAudio `WaveOutEvent` | n/a |

See [local-stack.md](local-stack.md) for setup, verify curls, GPU budget.

## Code layout

C# at repo root:

- [Program.cs](Program.cs) — main loop. Wake word → greeting → STT → command match or LLM fallback.
- [SpeechManager.cs](SpeechManager.cs) — facade around wake-word recognizer, Whisper STT, Kokoro TTS, and the pygame speech bubble.
- [LLMClient.cs](LLMClient.cs) — `LocalLLMService.GenerateResponse(string)`. OpenAI-compatible chat call to LM Studio, fed search hits from SearxNG.
- [SearxNGClient.cs](SearxNGClient.cs) — `SearxNGService.SearchAsync(query)`. Best-effort: a SearxNG outage returns empty, the LLM still answers.
- [STTClient.cs](STTClient.cs) — `WhisperSTTService.RecognizeOnceAsync(maxSeconds)`. Captures via NAudio with VAD-style silence detection, POSTs WAV to `/v1/audio/transcriptions`.
- [TTSClient.cs](TTSClient.cs) — `KokoroTTSService.SpeakAsync(text)` + `StopSpeaking()`. Patches Kokoro's streamed WAV header before NAudio plays it; `StopSpeaking()` cuts playback immediately.
- [PrayerTimesCalculator.cs](PrayerTimesCalculator.cs), [WeatherService.cs](WeatherService.cs), [Geolocator.cs](Geolocator.cs), [LightAutomator.cs](LightAutomator.cs), [PlaystationController.cs](PlaystationController.cs), [SMSController.cs](SMSController.cs), [Arduino.cs](Arduino.cs) — command handlers, all go through `speechManager.RecognizeOnceAsync()` / `Say()`.

Python alongside:

- [SpeechBubble.py](SpeechBubble.py) — pygame bubble; loaded via pythonnet. Untouched by the local-stack swap.
- [SMSService.py](SMSService.py) — Phone Link automation for SMS.
- [AutoRemotePlay.py](AutoRemotePlay.py) — PS Remote Play game-launch automation.

## Environment variables

**Required:**
- `WEATHERAPI_KEY` — OpenWeatherMap key (still cloud).

**Optional (defaults shown):**
- `LMSTUDIO_URL` — `http://localhost:1234/v1`
- `SEARXNG_URL` — `http://localhost:8080`
- `WHISPER_URL` — `http://localhost:8000`
- `WHISPER_MODEL` — `Systran/faster-whisper-large-v3`
- `KOKORO_URL` — `http://localhost:8880`
- `KOKORO_VOICE` — `am_onyx`
- `IP_ADDRESS:PLUG`, `IP_ADDRESS:SWITCH` — TP-Link Kasa LAN endpoints
- `CONTACTS_PATH` — JSON file mapping contact names → phone numbers

`SPEECH_KEY`, `SPEECH_REGION`, `SPEECH_ENDPOINT_ID`, `GEMINIAPI_KEY` are **not used** on this branch.

## Build and run

1. Bring up the local stack — see [local-stack.md](local-stack.md).
2. Open `Personal Assistant.sln` in Visual Studio, restore NuGet packages.
3. F5.

## Editing notes

- The wake-word `KeywordRecognizer` is the only Azure Speech component still in play and it's on-device — don't remove `Microsoft.CognitiveServices.Speech` from `packages.config` or the csproj.
- `Say(userInput, response)` is the unified TTS+bubble entrypoint. It schedules the synth on the threadpool and shows the bubble in parallel; the bubble retracts when the synth completes. Prefer this over calling `SynthesizeTextToSpeech` + `SpeechBubble` separately.
- Kokoro has **no SSML**. For pronunciation control, hand the synth a spelled-out transliteration (see `PrayerTimesCalculator.PrayerSpoken`).
- Kokoro-FastAPI streams its WAV with placeholder RIFF/data chunk sizes that NAudio's `WaveFileReader` rejects. `TTSClient.FixWavHeaderSizes` rewrites them in-place — keep that step.
- Whisper transcription is empty-string-on-failure. Callers check `string.IsNullOrEmpty(text)` instead of the old Azure `ResultReason.NoMatch`.
- `WarmUpAudioAsync` is still needed (Bluetooth / wireless DAC startup clipping). It hits Kokoro with `.`, also pre-loading the voice model on the server side.
