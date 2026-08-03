# Parakeet STT service

The speech-to-text the assistant listens through. Wraps NVIDIA
**parakeet-tdt-0.6b-v2** in the OpenAI transcription API so `STTClient.cs` talks
to it exactly the way it talked to faster-whisper-server.

> **`_coerce_text` gotcha — don't "simplify" it back.** NeMo's `transcribe()`
> returns a `Hypothesis`, and an **empty** transcript is normal here: the
> endpointer ships every clip that trips the VAD, so silence and noise arrive
> constantly. Coercing it with `getattr(out, "text", None) or str(out)`
> conflates "empty transcript" with "no text attribute", because `''` is falsy —
> it falls through to the repr and returns the entire object as the transcript.
> The assistant then answers `Hypothesis(score=0.0, y_sequence=tensor([]...` as
> if the user had said it, bubble and all. `test_coerce_text.py` locks this down
> and needs neither torch nor NeMo: `python stt-server/test_coerce_text.py`.

Picked by the bake-off (`bakeoff/stt/README.md`), and re-measured here against
the deployed service on the same 37-clip corpus:

| | WER | critical WER | keyword recall | median |
|---|---|---|---|---|
| **parakeet (this service, in Docker)** | 17.3% | **14.9%** | 75.4% | **229 ms** |
| parakeet, same code on the Windows host | 17.3% | 14.9% | 75.4% | 289 ms |
| whisper-large-v3-turbo | 17.7% | 16.7% | **77.2%** | 459 ms |

Containerising was expected to cost latency — CPU inference inside the WSL 2 VM
had every reason to be slower than native. It came out **60 ms faster**, with
byte-identical transcripts. Worth remembering before assuming a container tax.

Critical WER is the number that matters — it covers the words that become tool
arguments, where an error sends a text to the wrong person rather than costing a
cosmetic point. Parakeet also runs on the **CPU**, which hands whisper's ~2.2 GB
of VRAM back to the LLM and Kokoro.

## Run it

It's a container in the same stack as Kokoro and SearxNG:

```powershell
docker compose up -d stt
```

Set `CONTACTS_PATH` in `.env` first (copy `.env.example`) — compose mounts that
file read-only into the container and the boosting tree is built from it.

The image is CPU-only (`torch` CPU wheel, no CUDA runtime) and the model lives
on the `parakeet-models` volume, so it survives rebuilds. First start downloads
~2.4 GB; after that it loads in ~6 s, warms up, and serves on `:8001`.

```powershell
docker compose logs -f stt      # one line per utterance: latency, RTF, text
curl http://127.0.0.1:8001/health
```

To run it on the host instead — for debugging, or with the GPU build —
`.\stt-server\start.ps1` does the same thing outside Docker.

```json
{"status":"ok","model":"nvidia/parakeet-tdt-0.6b-v2","device":"cpu",
 "boost_alpha":2.0,"boost_phrases":31,"lexicon_terms":32}
```

**`boost_phrases: 0` means CONTACTS_PATH didn't resolve** and contact-name recall
just fell from 75% to 41.7%. It is the single most load-bearing line in that
response.

## Address it by IP, never by `localhost`

Found the hard way while this ran on the host: it bound IPv4 loopback, `localhost`
resolves to `::1` first on Windows, and every request waited for the IPv6 connect
to be refused before falling back — **2247 ms per transcription against 205 ms via
`127.0.0.1`**, for the same 264 ms of inference underneath. It looks exactly like
a slow model and is not one.

Docker publishes dual-stack, so the containerised service doesn't have the
problem. The defaults still use the IP, because `start.ps1` on the host does, and
because a hostname that is sometimes 2 seconds slower is not worth the elegance.

Inside the container `PARAKEET_HOST` is `0.0.0.0` — loopback there would make the
published port unreachable.

## Environment

| Variable | Default | |
|---|---|---|
| `CONTACTS_PATH` | — | contact names to boost toward; see above. In the container this is set for you and the host file is mounted from `.env` |
| `PARAKEET_HOST` | `127.0.0.1` (`0.0.0.0` in the image) | loopback only on the host — there is no auth on this |
| `PARAKEET_PORT` | `8001` | |
| `PARAKEET_MODEL` | `nvidia/parakeet-tdt-0.6b-v2` | v2 is English-only and beats multilingual v3 on every axis here |
| `PARAKEET_DEVICE` | `cpu` | `cuda` + `PARAKEET_HALF=1` is ~20 ms faster and costs 1.3 GB of VRAM |
| `PARAKEET_BOOST_ALPHA` | `2.0` | `0` disables boosting |
| `PARAKEET_CONTEXT_SCORE` | `1.5` | |
| `PARAKEET_LEXICON` | `1` | post-hoc vocabulary correction |

## What it does beyond running the model

Both of these are why the service exists rather than the client just POSTing
somewhere — they have to happen where the model is.

**Word boosting.** NeMo fuses a boosting tree into TDT decoding, biasing it
toward the contact list *at decode time*. It is the structural equivalent of the
decoder prompt whisper gets, and it is the only reason to accept NeMo's heavy
install over the much lighter `onnx-asr` build: it took contact-name recall from
41.7% to 75.0%, past whisper's 66.7%. The tuning window is narrow — at
`alpha >= 3` the decoder starts stuffing contact names into unrelated commands
("Turn off the bedroom light" → "Trenton Laythe Laythe Layth").

**Lexicon correction** (`lexicon.py`). A conservative post-hoc snap onto known
vocabulary for what boosting still missed. Contact names are only corrected in
the window after an SMS trigger word, a span that already is a known name is
never rewritten, and matching runs on a phonetic skeleton rather than raw
characters. Currently 6/6 intended corrections with 0/12 false positives on the
test set. `bakeoff/stt/run.py` imports this same module, so the bake-off scores
the correction code that actually ships.

## Going back to whisper

One environment variable — the request `STTClient.cs` sends is whisper-shaped
and this service simply ignores the fields that mean nothing to it:

```powershell
$env:STT_URL = "http://localhost:8000"
docker compose --profile whisper up -d whisper
```

## Files

| | |
|---|---|
| `parakeet_server.py` | the service |
| `lexicon.py` | vocabulary correction, shared with the bake-off |
| `Dockerfile` | CPU-only image |
| `start.ps1` | run it on the host instead of in Docker |
| `requirements.txt` | the same deps, for the host path |

## Re-measuring

The deployed service is scored by the same harness as every candidate:

```bash
python bakeoff/stt/run.py --engines parakeet-server,whisper
python bakeoff/stt/tail_sweep.py --engines parakeet-server
```
