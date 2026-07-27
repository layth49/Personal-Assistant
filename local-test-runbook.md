# Test runbook — simulated STS (sessions C/D/E)

Written so it can be followed with no assistant session open. Everything below
runs from the repo root.

## State this was left in

- **Kokoro: GPU** (24–31x realtime, ~1 GB VRAM). `DEVICE=cpu` + `USE_GPU=false`
  in [docker-compose.yml](docker-compose.yml) switches the *same image* to CPU
  with no pull, if VRAM ever has to be reclaimed — it costs ~2s of
  time-to-first-audio, so it's a last resort.
- **LM Studio: `qwen/qwen3-4b-2507`**, context 8192, loaded with `--gpu max`.
- Debug and Release are both built and current.
- `SpeechBubble.py` and `VoiceActivity.py` are deployed to
  `C:\Users\layth\LAITH\local\` (see the deployment note at the bottom — this
  matters every time you edit them).

## Clear the GPU first

This is the point of the exercise. Measured baseline load with the whole stack
idle was **19–41%**, and the largest single consumer was the **Claude desktop
app at 35.5%**. Before measuring anything:

- Close the Claude desktop app.
- Close Wallpaper Engine, browsers, and Visual Studio if you aren't debugging.

Confirm the card is actually quiet — this should read close to 0% with only the
containers idle:

```bash
nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total --format=csv
```

## 1. Re-run the LLM bake-off on a quiet GPU

**Start with `qwen3-4b-2507` — the current winner — before trying anything new.**
Its previous run reported 55 tok/s; on a contended card it measured 18 tok/s. The
same model re-run on a quiet GPU tells you how much of the slowness was
contention rather than the model, and re-baselines every other candidate.

For each model: load it in LM Studio (Developer → Start Server, context ≥ 8K),
then:

```bash
"bin/Release/Personal Assistant.exe" --bakeoff --runs 3
```

`--runs 3` samples detection three times per case, so you see routing *stability*
rather than one lucky pass. Results land in `bakeoff/llm/results/<model>.json`,
auto-labelled from LM Studio's model id. Only the LLM endpoint needs to be up.

Prior results, for comparison (contended GPU):

| Model | tool match | arg validity | detect mean | tok/s |
|---|---|---|---|---|
| qwen3-4b-2507 | **96.6%** | 100% | 1325ms | 55 |
| qwen2.5-7b-abliterated-v2 | 91.5% | 100% | 4660ms | 14.5 |
| qwen3-1.7b | 81.9% | 100% | 4143ms | 101 |
| lfm2.5-1.2b | 69.5% | 100% | 526ms | 102.5 |

Worth adding as new candidates: **qwen3-4b-instruct-2507**, **granite-4.0-h-3b**,
**llama-3.2-3b-instruct**. Avoid anything `-thinking` or `-reasoning`: reasoning
tokens are pure latency for voice.

## 2. Test the assistant

**Wear a headset.** On speakers Kokoro's output reaches the mic, the VAD hears
it, and the assistant interrupts itself in a loop. Echo cancellation is Session G.

The app is a `WinExe` with no console attached, so you must redirect stdout or
you'll see none of the diagnostics:

```bash
cd "bin/Release" && "./Personal Assistant.exe" > run.log 2>&1
```

Watch it in a second terminal:

```bash
tail -f "bin/Release/run.log"
```

What to exercise, and the line that proves it:

| Test | Say | Look for |
|---|---|---|
| Barge-in | "Hey 49" → "explain how a jet engine works", then talk over it | `[barge-in] user spoke over the reply -> cutting off`, then your new utterance is answered |
| Follow-ups | Ask again with no wakeword | It answers; after 12s idle, `[loop] no follow-up — closing the conversation` |
| First audio | any reply | `[tts] first audio at NNNms (NNNms buffered)` and `first-audio=` in the `[latency]` line |
| Streaming bubble | a multi-sentence reply | bubble appears on sentence one and grows in place |
| Regression | "turn off the lights", a reminder firing, "exit" | tool runs, reminder speaks, clean shutdown |

Ignore `RuntimeBinderException` lines under the VS debugger — that's pythonnet's
dynamic binder probing members and catching them internally. Harmless.

## 3. Knobs, if something is off

| Symptom | File | Knob |
|---|---|---|
| Cuts you off mid-sentence | [ContinuousListener.cs](ContinuousListener.cs) | `TrailingSilenceSamples` (now 1000ms) |
| Barge-in won't trigger / triggers on noise | [ContinuousListener.cs](ContinuousListener.cs) | `OnsetThreshold` (0.5), `OnsetFrames` (2) |
| Transcripts poor / clipped words | [ContinuousListener.cs](ContinuousListener.cs) | `PreRoll` (500ms) |
| Audio stutters or drops out | [TTSClient.cs](TTSClient.cs) | `PlaybackLead` (700ms), `DesiredLatency` (250ms) — raise both for Bluetooth |
| Too many Kokoro requests per reply | [LLMClient.cs](LLMClient.cs) | `LaterMinChars` (140) — raise for fewer, larger chunks |
| First audio too slow | [LLMClient.cs](LLMClient.cs) | `FirstMinChars` (30) — lower for an earlier first chunk |

Count TTS/STT requests for a turn:

```bash
docker logs --since 5m kokoro 2>&1 | grep -c "POST /v1/audio/speech"
```

## Deployment gotcha — read before editing any .py

The app imports Python modules from **`C:\Users\layth\LAITH\local\`**, not from
this repo: `Program.Main` appends that directory to `sys.path`, and no `.py` is
copied to the output folder. **Editing `SpeechBubble.py` or `VoiceActivity.py`
here does nothing until you copy it across.** This silently made two sessions'
worth of bubble work inert before it was spotted.

```bash
cp SpeechBubble.py VoiceActivity.py "C:/Users/layth/LAITH/local/"
```

## Known-unverified

Nobody has yet spoken into a live mic with this build. Component tests cover the
VAD, endpointing, chunking, the TTS queue and the bubble; a startup smoke test
covers Python init, Silero load and mic open. **Live barge-in over real speech is
the gap** — that's what section 2 is for.
