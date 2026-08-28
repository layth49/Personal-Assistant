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
| LLM | Gemini 2.5 Flash + Google-search grounding | LM Studio (`qwen/qwen3-4b-2507`) + SearxNG | `http://127.0.0.1:1234/v1`, `http://localhost:8080` |
| STT | Azure Speech-to-Text | Parakeet TDT 0.6B v2 on CPU (`stt-server/`) | `http://127.0.0.1:8001` |
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
- [STTClient.cs](STTClient.cs) — `SpeechToTextService.TranscribeAsync(wavBytes)`. POSTs WAV to `/v1/audio/transcriptions`. Transcription only; the mic belongs to [ContinuousListener.cs](ContinuousListener.cs), which runs Silero VAD, endpoints utterances, and gates speaker echo (with [EchoGuard.cs](EchoGuard.cs) for the transcript half of that).
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
- `LMSTUDIO_URL` — `http://127.0.0.1:1234/v1` (not `localhost` — LM Studio is a host process binding IPv4 only, same trap as the STT service)
- `SEARXNG_URL` — `http://localhost:8080`
- `STT_URL` — `http://127.0.0.1:8001` (the Parakeet service; `WHISPER_URL` is still read as a fallback). Not `localhost` — see the editing notes.
- `KOKORO_URL` — `http://localhost:8880`
- `KOKORO_VOICE` — `am_onyx`
- `IP_ADDRESS:PLUG`, `IP_ADDRESS:SWITCH` — TP-Link Kasa LAN endpoints
- `CONTACTS_PATH` — JSON file mapping contact names → phone numbers
- `LAITH_ECHO_GATE` — `auto` (engage the barge-in energy gate only once speaker bleed is observed) / `on` / `off`
- `LAITH_ECHO_MARGIN` — how far above the measured speaker bleed a frame must sit to count as you talking, default `1.5`. Lower if barge-in needs shouting, raise if the assistant cuts itself off
- `LAITH_ECHO_TEXT_GATE` — `off` disables the transcript echo check (debugging only)

`SPEECH_KEY`, `SPEECH_REGION`, `SPEECH_ENDPOINT_ID`, `GEMINIAPI_KEY` are **not used** on this branch.

## Build and run

1. Bring up the local stack — see [local-stack.md](local-stack.md).
2. Open `Personal Assistant.sln` in Visual Studio, restore NuGet packages.
3. F5.

## Editing notes

- The wake-word `KeywordRecognizer` is the only Azure Speech component still in play and it's on-device — don't remove `Microsoft.CognitiveServices.Speech` from `packages.config` or the csproj.
- `Say(userInput, response)` is the unified TTS+bubble entrypoint. It schedules the synth on the threadpool and shows the bubble in parallel; the bubble retracts when the synth completes. Prefer this over calling `SynthesizeTextToSpeech` + `SpeechBubble` separately.
- Kokoro has **no SSML**. For pronunciation control, hand the synth a spelled-out transliteration (see `PrayerTimesCalculator.PrayerSpoken`).
- **Kokoro says emoji out loud** — hand it `😊` and it pronounces "smiling face with smiling eyes". `KokoroTTSService.StripUnspeakable` removes them inside `RequestWavAsync`, the one place both the one-shot and streaming paths go through, so the bubble still shows them and the voice never says them. Don't route around it: those invented words also come back through the mic as text no reply ever contained, which is what let a whole reply escape the echo gate.
- Kokoro-FastAPI streams its WAV with placeholder RIFF/data chunk sizes that NAudio's `WaveFileReader` rejects. `TTSClient.FixWavHeaderSizes` rewrites them in-place — keep that step.
- **A local model will sometimes hand back a tool call as plain text** rather than through the `tool_calls` channel — most often when the tool it wants isn't registered, as `send_sms` isn't whenever `CONTACTS_PATH` fails to resolve. Streamed straight to Kokoro it gets read aloud ("…arguments, recipient, message, slash tool call"). `LocalLLMService.ToolCallStart` truncates the stream at the marker; it also catches bare `{"name": …, "arguments": …}` with no marker. When that leaves *nothing* to say the reply falls back to a spoken line — silence is the worst outcome, since the user gets no audio, no bubble, and no idea they were heard. Cases in `bakeoff/echo/ToolCallTest.cs`.
- Transcription is empty-string-on-failure. Callers check `string.IsNullOrEmpty(text)` instead of the old Azure `ResultReason.NoMatch`.
- **`SearxNGService` is best-effort for chat and load-bearing for verification, and those need opposite failure behaviour.** An outage returns an empty list so the LLM still answers from its own knowledge — right for `web_search`, and exactly wrong for "has this event happened yet", where answering from training data is the failure the feature exists to prevent. `LocalLLMService.VerifyEventAsync` therefore **refuses** on an empty result set (`EventVerdict.Unknown`) instead of falling through to the model. Consequence: a silently-broken SearxNG presents as an event watch that waits forever, never as a wrong answer. Check it with `curl 'http://localhost:8080/search?q=test&format=json'` — a 403 means the container isn't reading a `settings.yml` with `formats: [html, json]` and `limiter: false`. **Verify with `docker inspect searxng` what it actually mounted**: on 2026-08-11 the running container had been created by a `docker-compose.yml` in the *main* worktree that no longer exists, so it was mounting main's stock 70 KB settings (html only, no `limiter.toml`) while the correct config sat unread in this worktree. `docker compose` also interpolates the **whole** compose file even when you name one service, so `docker compose up -d searxng` needs `.env` (gitignored) to define `CONTACTS_FILE`.
- **`VerifyEventAsync` splits into retrieve-then-judge on purpose** (`JudgeEventAsync` is the second half, public). A SearxNG outage and a model answering in the wrong shape both surface as `Unknown`, and telling them apart is most of debugging. It also means the prompt can be tested with the search stack down — see `bakeoff/resume/`.
- **The verification call must keep `reasoning_effort = "none"`.** Same lesson as the router: a model that spends its budget thinking returns empty `content`, which parses as `Unknown`, so the watch re-checks forever and the log blames the search. `/no think` in the prompt does not do this reliably.
- **Address a local service as `127.0.0.1`, never `localhost`.** Learned on the STT service: the name resolves to `::1` first on Windows, so against an IPv4-only socket each transcription waited ~2s for the IPv6 connect to be refused — 2247 ms vs 205 ms for identical work, presenting as a slow model. Docker publishes dual-stack and hides it; anything run directly on the host does not.
- The STT service is a container built from `stt-server/` (`docker compose up -d stt`; `.\stt-server\start.ps1` runs the same thing on the host for debugging). `curl http://127.0.0.1:8001/health` and check `boost_phrases` isn't 0; 0 means `CONTACTS_PATH` didn't resolve, which costs ~33 points of contact-name recall. Compose reads that path from `.env`, the app reads it from the user environment — both have to be right.
- Contact names reach the decoder differently per engine: whisper takes them as a prompt (which `TranscribeAsync` still sends), Parakeet as a decode-time boosting tree built server-side from the same file. Both read `CONTACTS_PATH`.
- `WarmUpAudioAsync` is still needed (Bluetooth / wireless DAC startup clipping). It hits Kokoro with `.`, also pre-loading the voice model on the server side. `LocalLLMService.WarmUpAsync` is its LM Studio twin (a one-token completion — the first request after a model load is much slower); `Program.Main` runs both in one `Task.WhenAll` before the first wakeword can fire.
- **There is exactly ONE `SpeechService` — `SpeechService.Current`. Never `new` one in a handler.** Six of them used to (`LightAutomator`, `PlaystationController`, `PrayerTimesCalculator`, `SMSController`, `WeatherService` ×2) and a second instance is silently useless in both directions: its `BeginSpeaking` updates a `ContinuousListener` that was never started, so the *real* listener never learns the assistant is talking and every prompt that handler speaks escapes the echo gate wholesale; and its `RecognizeOnceAsync` waits on that same dead listener, so it always times out. Observed end to end: the SMS flow got `""` back from dictation and **actually sent an empty text to a real number**, while its escaped prompts queued as turns and the assistant held a minute-long conversation with itself. Handlers reference it through a `speechManager` property that returns `SpeechService.Current`, so there is no construction-order trap.
- **`MaxUtteranceSamples` (20s) is a CHUNK boundary, not a turn boundary.** Hitting it transcribes the audio so far and keeps recording; only silence ends a turn. It used to end the turn, so reading a long list aloud was cut mid-sentence, the fragment answered while the user was still talking, and the remainder became a second turn — one question, three bubbles. The banked chunks are stitched in `TranscribeAndPublishAsync` under `transcribeGate` (two transcriptions in flight can otherwise finish out of order). Two stranding traps the guards exist for: a tail shorter than `MinSpeechSamples` must NOT be discarded as a blip once chunks are banked, and a blank tail transcript must flush the bank rather than return. Cases in `bakeoff/echo/ContinuationSim.cs`.
- **Every path that produces assistant audio must go through `SpeechService.BeginSpeaking` / `EndSpeaking`** (they're already inside `sayGate` on all of them, so `speaking` can never be clobbered by an overlapping speaker). Skipping them is not a cosmetic omission: barge-in won't fire for that audio, and — worse — the listener won't know to echo-check what it hears, so the assistant can end up answering itself.
- **On loud speakers the wakeword is the ONLY barge-in that works** — `SpeechService.StartWakewordWatch` runs the on-device spotter for the length of every streamed reply, so saying `49` cuts it. The level gate can't help there and neither can the text gate: once the mic hears the assistant continuously the VAD never gets a gap, so the whole reply arrives as ONE utterance with the user's interruption buried in it, and the echo check drops the lot. Text can't separate them either, because a reply reuses the question's own vocabulary ("tell me how a jet engine works" vs a reply opening "A jet engine works by…" is a verbatim four-word run). Two things this needs: `replyNamesAssistant` suppresses the trigger while the reply itself says "49"/"Laith" (otherwise it cuts itself off), and `ContinuousListener.RestartCapture()` drops the mid-capture buffer on a wakeword cut, or the command *after* the wakeword gets dropped as part of the echo it's glued to.
- **Echo handling is two independent layers, and both are load-bearing.** On speakers, Kokoro's output reaches the mic; without help the VAD fires, barge-in cuts the reply, the echo is transcribed, and answering it makes more audio to hear.
  1. `ContinuousListener`'s energy gate — while the assistant is audible, a frame only counts as barge-in if it's ≥`BargeInMargin` (1.5, `LAITH_ECHO_MARGIN`) above a **decaying peak** of the bleed. A peak, not an average: an average sits mid-dynamics, so the assistant's own loud syllables clear it. Two things about it are non-obvious and were both learned the hard way:
     - **The gate keys off `KokoroTTSService.PlaybackStarted`, never off the microphone.** Deriving "the speakers are on" from the mic level always loses a race it can't win: Silero recognises the assistant's voice before the frame level has risen far enough to be told apart from room noise, so the gate was still inactive when the echo's onset fired — and that happens at *any* volume, which is why turning the speakers down didn't help. The TTS knows the answer exactly; it reports it.
     - **Warm-up and the bleed measurement are anchored to when audio is AUDIBLE**, which is separate again: a streamed reply starts playing 1-2s after `BeginAssistantSpeech` (LLM → synth → playback lead), and playback itself precedes sound at the mic by a device buffer. Counting from `BeginAssistantSpeech` ran the whole calibration window in silence — it measured a "bleed peak" of 0.0089 against a room of 0.0010.
     - **The test runs on every frame of the utterance, not just its onset**, and needs `BargeInSustainFrames` (3, ~90ms) consecutive frames over the bar. The onset frame is the first ~60ms of the first syllable — level still rising, VAD only half-convinced (measured p≈0.5-0.6 against a 0.7 bar) — so judging there rejected barge-ins no amount of extra volume would have saved. Sustain is what stops one loud syllable of bleed counting.
     - **Ambient is the room's NOISE FLOOR — a minimum tracker, never a peak.** A peak latched onto the user's wakeword, which is spoken while the listener is unarmed, so the `!inSpeech` guard protecting it was inert exactly when it was needed: ambient climbed to 0.22 and the barge-in bar (`ambient x2`) became 0.4475, beyond anything a human could clear. A minimum ignores anything loud by construction.
     - **The bar is measured over warm-up only, then carried across replies** (`sessionBleedFloor`, faded 10% per reply so turning the volume down is followed). Bleed level is a property of the volume knob, not of one reply. Three attempts at learning it *during* the reply all failed, in different ways: snapshotted at the utterance start it can't follow a reply that gets louder (floor 0.1666 vs bleed reaching 0.2596 → self-cut); tracked live it chases the user's own attack up so they never get ahead of it; taught only from below-bar frames it still chases, because the quiet leading edge of the user's speech is below the bar by definition. A sustain count is also meaningless against a moving bar, since the bar moves with you.
     It gates the `SpeechOnset` **event only** — the utterance is still captured, so a genuine barge-in the gate was too strict about is answered late rather than lost (`SpeechService.OnUtteranceReady` cuts the reply then).
  2. `EchoGuard.IsEcho` on the finished transcript — the backstop that guarantees an echo never becomes a turn. Two independent tests, because they fail on different things: content-word overlap (survives the STT reordering or dropping words) **or** a ≥4-word contiguous run (survives one garbled word tanking the overlap score — that exact case, `"Mohsin, your own personal assistant"` vs `"…your own personal assistant!"`, scored 0.75 and got answered). `IsEcho` is tried against **both hyphen readings** — the STT and the reply text disagree about where a word divides, and one extra boundary breaks the run and the overlap at once (`"to-day"` vs `"today"` scored run=3 overlap=0.33 and got answered). Both readings are needed, not one: `"forty-two"` only matches `"12:42"` while the hyphen still splits. `Tokenize` also **expands digits into the words they're spoken as** (`11:03` → `eleven oh three`), because the reply text is written and the echo comes back spoken — without it every numeric tool (clock, timers, weather, prayer times) was unprotected. The reference is a **rolling window** of the last ~800 chars spoken, NOT just the current reply: an echo is routinely still mid-capture when the next reply starts (trailing silence expires ~800ms after the audio stops), and clearing the reference there let a verbatim echo through. An utterance heard over speech and *kept* logs `[echo] kept … run=…/… overlap=…` — that's the line to read when something escapes.
  `LAITH_ECHO_GATE` = `auto` (default; layer 1 engages only once bleed has actually been observed, so a headset keeps instant barge-in and pays nothing) / `on` / `off`. `Program.Main` opens the mic **before** `WarmUpAudioAsync` on purpose: the warm-up doubles as the calibration pass, so the first reply of a session is already gated instead of interrupting itself. `LAITH_ECHO_TEXT_GATE=off` disables layer 2 — debugging only. Both layers log with an `[echo]` prefix. Re-run `bakeoff/echo/` after touching any threshold; `GateSim.cs` duplicates the constants deliberately.
