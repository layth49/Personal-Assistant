# Test runbook — simulated STS (sessions C/D/E/F/G)

Written so it can be followed with no assistant session open. Everything below
runs from the repo root.

## State this was left in

- **Kokoro: GPU** (24–31x realtime, ~1 GB VRAM). `DEVICE=cpu` + `USE_GPU=false`
  in [docker-compose.yml](docker-compose.yml) switches the *same image* to CPU
  with no pull, if VRAM ever has to be reclaimed — it costs ~2s of
  time-to-first-audio, so it's a last resort.
- **LM Studio: `qwen/qwen3-4b-2507`**, context 8192, loaded with `--gpu max`.
- **STT: Parakeet on the CPU** (container `parakeet-stt`, built from
  `stt-server/`), not the whisper container — that one is behind a `whisper`
  profile now and stays stopped. `docker compose up -d` brings STT up with the
  rest. It needs `CONTACTS_PATH` in `.env`.
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

Session G added echo handling, so **speakers are now in scope** — see section 2b.
A headset is still the cleanest way to test everything else, and is what the
barge-in row below assumes.

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
| STT swap (session F) | "send a text to <contact>" | the name comes back right; `docker compose logs -f stt` shows `[stt] ~270ms rtf=0.1x`, and `(was: ...)` if the lexicon corrected it |

### Getting a readable log

Under F5 the Output window interleaves the assistant's lines with debugger
annotations — `Exception thrown: 'Microsoft.CSharp.RuntimeBinder.RuntimeBinderException'`,
`Loaded '…dll'`, `The thread NNNNN has exited`. None of it comes from the app.
**Right-click inside the Output window** and untick *Exception Messages*,
*Module Load Messages*, *Thread Exit Messages* and *Process Exit Messages* (same
switches under Tools → Options → Debugging → Output Window).

Running outside the debugger with the redirect above avoids them entirely, which
is the better option when you want a log to hand to someone.

The `RuntimeBinderException`s are harmless either way: pythonnet's `dynamic` call
sites make the C# binder probe for a member, miss, and fall back to Python's own
dispatch. First-chance, caught internally, and cached per call site after the
first hit — which is why they arrive in small bursts rather than one per frame.

Session G also warms LM Studio at launch: `[llm] warm-up done in NNNms` should
appear before the first wakeword, and the first real turn's `llm=` should be in
the same range as later ones rather than seconds worse.

## 2b. Test on speakers (Session G — the unverified part)

Switch output to speakers, set them to the volume you'd actually use, and repeat
section 2. Everything with an `[echo]` prefix is this layer talking.

| Test | Do | Look for |
|---|---|---|
| Bleed is detected | just launch it | `[echo] speakers audible …ms after the reply started`, then `[echo] bleed floor … (session …, barge-in above …)`, then `[echo] bleed detected` — all **before the first wakeword**. **Sanity-check the floor** against the `rms` figures in later `[echo]` lines: they should be the same order. A floor near 0.01 with `rms` in the 0.1s means calibration ran during silence and the gate will misfire |
| Emoji aren't spoken | ask something that draws an emoji | the voice does **not** say "smiling face with smiling eyes"; the bubble still shows the emoji |
| STT never returns junk | let a silent/noisy clip get endpointed | **no** `RECOGNIZED: Hypothesis(score=...)` line. That was the STT server returning the repr of an empty NeMo result as the transcript, which then reached the LLM and the bubble |
| No self-interruption | ask for a long reply, stay silent | reply plays to the end; **no** `[barge-in]` line. `[echo] onset held as bleed` is the gate working, not a fault |
| No self-answering | as above | **no** `RECOGNIZED:` line carrying the assistant's own words. `[echo] dropped self-heard utterance: "…"` is the backstop catching one |
| **Wakeword barge-in** | say **"49"** over a long reply, then your command | `[barge-in] wakeword heard over the reply -> cutting off`, audio stops, and the command after "49" is answered. **This is the one that works on loud speakers** — it matches an acoustic pattern rather than measuring loudness, so bleed can't defeat it |
| Level barge-in | talk over a long reply, **normal voice — don't raise it** | `[echo] barge-in allowed NNNms into the utterance` then `[barge-in] … cutting off`. Only expected at low volume; above ~30 the gate correctly declines and you need the wakeword |
| Late barge-in fallback | talk over it quietly | `[barge-in] utterance landed mid-reply -> cutting off`. Note this only fires when your speech endpoints *separately* — on loud speakers it usually can't, which is why the wakeword path exists |

If `[echo] bleed detected` does *not* appear at startup on speakers, the
calibration pass didn't see the warm-up — check the mic is the same device the
speakers are near, and set `LAITH_ECHO_GATE=on` to gate unconditionally.

**Tuning is one environment variable**, no rebuild: `LAITH_ECHO_MARGIN` (default
1.5) is how far above the measured bleed a frame must sit to count as you.

**If barge-in never lands at all on speakers, don't reach for the margin — say
"49".** Once the speakers are loud enough that the mic hears the assistant
continuously, the VAD never gets a gap to endpoint on, so the whole reply comes
back as ONE utterance with your interruption buried inside it and the echo check
drops the lot. No threshold fixes that, and text can't separate them either: a
reply reuses the question's own vocabulary, so "tell me how a jet engine works"
against a reply opening "A jet engine works by…" is a verbatim four-word run.

- **Barge-in needs shouting** → lower it (1.3, 1.2). Confirm the gate is the
  cause first with `LAITH_ECHO_GATE=off`. Check the `[echo] onset held` lines:
  if `rms` is close to `floor` you're being drowned out and lowering will help;
  if `p=` is well under 0.70 the VAD isn't convinced it's speech yet and the
  deferred test should pick it up a few frames later anyway.
- **A reply cuts itself off** → the tell is an `[echo] barge-in allowed` line
  followed by `[echo] dropped self-heard utterance`: it cut, then worked out the
  thing it cut for was itself. First check `[echo] bleed peak measured at …`
  against the `rms` values in the same run — if the peak is much lower, the
  floor is wrong and no margin will save it. If the peak looks right, raise the
  margin (1.8, 2.0).

### What "works at any volume" actually means

`bakeoff/echo/GateSim.cs` prints a sweep over volume 20-100. Two different
guarantees, and only one of them is absolute:

- **Never cuts itself off — at any volume.** This is the invariant, and the
  sweep fails the build if it breaks anywhere in the range.
- **Instant barge-in — only while there's headroom.** Measured, bleed is
  ~0.04 rms at volume 20 and ~0.30 at volume 40, while a normal speaking voice
  is ~0.25-0.35. Below roughly volume 30 the two are separable and barge-in cuts
  within ~200ms. Above it they overlap, and no threshold separates overlapping
  distributions — so the gate deliberately declines rather than misfire, and
  barge-in falls through to the **late path**: your utterance is transcribed,
  echo-checked, and cuts the reply then, about a second later.

So it works at every volume; it's just *instant* at low volume and *about a
second* at high volume. Getting instant barge-in at high volume needs either a
physical change (speakers down, or the mic further from them) or real acoustic
echo cancellation, which is the one thing Session G deliberately didn't build.

If the assistant answers its own words, the `[echo] kept (heard over speech)`
line prints the two scores that let it through. Capture that plus the
`RECOGNIZED:` text and add the pair to `bakeoff/echo/EchoGuardCases.cs` before
touching `ContainmentThreshold` — that file exists so a tuning change can't
quietly start ignoring real commands.

## 3. Knobs, if something is off

| Symptom | File | Knob |
|---|---|---|
| Cuts you off mid-sentence | [ContinuousListener.cs](ContinuousListener.cs) | `TrailingSilenceSamples` (now 800ms — the sweep in `bakeoff/stt/tail_sweep.py` says 1000 buys ~0.5 WER points, 600 costs ~2) |
| Every turn is ~2s slower than it should be | [STTClient.cs](STTClient.cs) | `STT_URL` says `localhost` — must be `127.0.0.1`; the name costs 2s per request on Windows |
| Contact names never transcribe right | environment | `curl http://127.0.0.1:8001/health` — `boost_phrases: 0` means `CONTACTS_PATH` is broken |
| Barge-in won't trigger / triggers on noise | [ContinuousListener.cs](ContinuousListener.cs) | `OnsetThreshold` (0.5), `OnsetFrames` (2) |
| On speakers: assistant interrupts itself | environment | raise `LAITH_ECHO_MARGIN` (default 1.5) |
| On speakers: your barge-ins are ignored | environment | lower `LAITH_ECHO_MARGIN`; `LAITH_ECHO_GATE=off` to confirm the gate is the cause |
| Assistant answers its own words | [EchoGuard.cs](EchoGuard.cs) | `ContainmentThreshold` (0.8) — add the failing pair to `bakeoff/echo/EchoGuardCases.cs` first |
| It ignores something you really said | [EchoGuard.cs](EchoGuard.cs) | same threshold, other direction; `StopWords` / `BargeInWords` if a specific word is the problem |
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

Session G is verified everywhere it can be without a room: `bakeoff/echo/` covers
the text gate against the real `EchoGuard.cs` (24 cases, including the commands
that legitimately reuse the reply's words) and the energy gate against synthetic
level traces for headset / speakers / loud-speakers. **What no test covers is a
real speaker in a real room** — the levels there are the whole question, so
section 2b is the one that matters.

Session F is verified everywhere it can be without a voice: the deployed STT
service is scored by the bake-off harness on 37 recorded clips, the LLM by the
60-case harness through the real dispatch path, and the app builds and links
against the new endpoint. What no test covers is the two of them in one live
turn — mic to Parakeet to qwen3-4b to Kokoro.
