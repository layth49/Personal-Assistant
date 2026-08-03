"""Parakeet TDT 0.6B as an OpenAI-compatible transcription service.

The bake-off (`bakeoff/stt/README.md`) picked `parakeet-boosted` on CPU over the
incumbent faster-whisper-server: better on the numbers that decide whether a
tool call is right (critical WER 14.9% vs 15.5%, contact-name recall 75.0% vs
66.7%), ~40% faster (~275 ms vs ~466 ms median), and it uses no VRAM at all,
which hands whisper's ~1.7 GB back to the LLM and Kokoro.

This wraps that exact configuration in the API the app already speaks, so
`STTClient.cs` needs only a different URL and can be pointed back at whisper by
changing one environment variable.

Three things have to be here rather than in the client, because they are all
decode-time or transcript-time behaviour:

  * **Word boosting** - NeMo fuses a boosting tree into TDT decoding, biasing it
    toward the contact list at decode time. It is the structural equivalent of
    whisper's decoder prompt and the reason NeMo is used instead of the much
    lighter onnx-asr build. `alpha=2.0 / context_score=1.5` took name recall
    from 41.7% to 75.0%; past roughly alpha=3 the decoder starts stuffing
    contact names into unrelated commands.
  * **Lexicon correction** - a conservative post-hoc snap onto known vocabulary
    (see lexicon.py), applied to whatever boosting still missed.
  * **Serialisation** - one model, one lock. NeMo's transcribe() is not
    re-entrant, and the assistant only ever has one utterance in flight.

Run it:

    python stt-server/parakeet_server.py          # or start.ps1

Environment:

    PARAKEET_HOST           127.0.0.1    (clients must use the IP, not "localhost")
    PARAKEET_PORT           8001
    PARAKEET_MODEL          nvidia/parakeet-tdt-0.6b-v2
    PARAKEET_DEVICE         cpu          (cuda for the fp16 GPU build)
    PARAKEET_BOOST_ALPHA    2.0          (0 disables boosting)
    PARAKEET_CONTEXT_SCORE  1.5
    PARAKEET_LEXICON        1            (0 disables the post-hoc correction)
    CONTACTS_PATH           - the contact names to boost toward
"""

import io
import json
import os
import sys
import tempfile
import threading
import time
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import lexicon  # noqa: E402

# Per-request lines are the only view into what the assistant actually heard,
# so they have to survive being piped to a log file.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(line_buffering=True, errors="replace")
    except Exception:
        pass

MODEL_NAME = os.environ.get("PARAKEET_MODEL", "nvidia/parakeet-tdt-0.6b-v2")
DEVICE = os.environ.get("PARAKEET_DEVICE", "cpu")
HALF = os.environ.get("PARAKEET_HALF", "1" if DEVICE != "cpu" else "0") == "1"
BOOST_ALPHA = float(os.environ.get("PARAKEET_BOOST_ALPHA", "2.0"))
CONTEXT_SCORE = float(os.environ.get("PARAKEET_CONTEXT_SCORE", "1.5"))
USE_LEXICON = os.environ.get("PARAKEET_LEXICON", "1") == "1"
PORT = int(os.environ.get("PARAKEET_PORT", "8001"))
HOST = os.environ.get("PARAKEET_HOST", "127.0.0.1")

# Terms worth boosting beyond the contact list. Same set the bake-off used.
JARGON = ["Arduino", "Home Assistant", "PlayStation", "Remote Play"]

_model = None
_vocabulary = []
_phrases = []
_lock = threading.Lock()


def load_contact_names():
    """Contact names from CONTACTS_PATH - the same file STTClient.cs reads."""
    path = os.environ.get("CONTACTS_PATH")
    if not path or not os.path.isfile(path):
        print("[stt] CONTACTS_PATH not readable ({}) - no name boosting"
              .format(path or "unset"))
        return []
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        return list(data.keys()) if isinstance(data, dict) else []
    except Exception as exc:
        print("[stt] could not read CONTACTS_PATH: {}".format(exc))
        return []


def load_model():
    global _model, _vocabulary, _phrases

    from nemo.collections.asr.models import ASRModel

    started = time.time()
    # Load to CPU, cast, then move. Loading fp32 straight to CUDA and halving
    # afterwards strands the fp32 blocks in torch's caching allocator, so the
    # card keeps reporting the fp32 peak (2.6 GB instead of 1.2 GB).
    _model = ASRModel.from_pretrained(model_name=MODEL_NAME, map_location="cpu")
    if HALF:
        _model = _model.half()
    if DEVICE != "cpu":
        _model = _model.to(DEVICE)
    _model.eval()

    contacts = load_contact_names()
    _phrases = [p.strip() for p in contacts + JARGON if p and p.strip()]

    if BOOST_ALPHA > 0 and _phrases:
        _apply_boosting(_phrases)
        print("[stt] boosting on: {} phrases, alpha={} context_score={}"
              .format(len(_phrases), BOOST_ALPHA, CONTEXT_SCORE))
    else:
        print("[stt] boosting off")

    if USE_LEXICON:
        _vocabulary = lexicon.build_vocabulary(contacts)
        print("[stt] lexicon on: {} terms".format(len(_vocabulary)))

    print("[stt] {} loaded on {} in {:.1f}s".format(
        MODEL_NAME, DEVICE, time.time() - started))


def _apply_boosting(phrases):
    from omegaconf import OmegaConf
    from nemo.collections.asr.parts.context_biasing import BoostingTreeModelConfig

    cfg = OmegaConf.structured(_model.cfg.decoding)
    cfg.strategy = "greedy_batch"
    cfg.greedy.boosting_tree = OmegaConf.structured(
        BoostingTreeModelConfig(
            key_phrases_list=list(phrases),
            context_score=CONTEXT_SCORE,
            use_triton=False,          # triton has no Windows build
        ))
    cfg.greedy.boosting_tree_alpha = BOOST_ALPHA
    _model.change_decoding_strategy(cfg)


def _coerce_text(out):
    """NeMo's transcribe() returns str, Hypothesis, or a list of either.

    An EMPTY transcript is a normal answer here — the endpointer ships anything
    that trips the VAD, so silence and noise arrive routinely. It must not be
    confused with "this object has no text attribute". `getattr(out, "text",
    None) or str(out)` did exactly that: an empty `Hypothesis.text` is falsy, so
    it fell through to the repr and returned the entire object as the
    transcript, which the assistant then answered as if the user had said it.
    """
    if isinstance(out, (list, tuple)):
        out = out[0] if out else ""
    if isinstance(out, str):
        return out.strip()
    text = getattr(out, "text", None)
    if text is None:
        # Never fall back to str(out): returning a repr downstream is worse in
        # every way than returning nothing, because nothing is handled.
        print("[stt] transcribe() returned {} with no .text - treating as empty"
              .format(type(out).__name__))
        return ""
    return str(text).strip()


def transcribe_wav(wav_bytes):
    """WAV bytes -> transcript. Serialised: one model, one utterance at a time."""
    # NeMo reads from disk. The temp file lives for the length of one call.
    fd, path = tempfile.mkstemp(suffix=".wav")
    try:
        with os.fdopen(fd, "wb") as fh:
            fh.write(wav_bytes)
        with _lock:
            raw = _coerce_text(
                _model.transcribe([path], batch_size=1, verbose=False))
    finally:
        try:
            os.remove(path)
        except OSError:
            pass

    text = lexicon.correct(raw, _vocabulary) if _vocabulary else raw
    return text, raw


def wav_seconds(wav_bytes):
    try:
        with wave.open(io.BytesIO(wav_bytes), "rb") as w:
            return w.getnframes() / float(w.getframerate())
    except Exception:
        return None


def warm_up():
    """First call carries a one-off cost; spend it before the app arrives."""
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16000)
        w.writeframes(b"\x00\x00" * 8000)     # 0.5s of silence
    started = time.time()
    try:
        transcribe_wav(buf.getvalue())
        print("[stt] warm-up done in {:.0f}ms".format(
            (time.time() - started) * 1000))
    except Exception as exc:
        print("[stt] warm-up failed: {}".format(exc))


# --------------------------------------------------------------------------
# HTTP surface: the subset of the OpenAI audio API that STTClient.cs uses.
# --------------------------------------------------------------------------

from contextlib import asynccontextmanager  # noqa: E402

from fastapi import FastAPI, File, UploadFile  # noqa: E402
from fastapi.responses import JSONResponse  # noqa: E402


@asynccontextmanager
async def lifespan(_app):
    # Blocking on purpose: /health must not answer "ok" before the model is
    # loaded, and nothing should be transcribed until the warm-up has absorbed
    # the one-off first-call cost.
    load_model()
    warm_up()
    yield


app = FastAPI(title="parakeet-stt", lifespan=lifespan)


@app.get("/health")
def health():
    return {"status": "ok" if _model is not None else "loading",
            "model": MODEL_NAME, "device": DEVICE,
            "boost_alpha": BOOST_ALPHA if _phrases else 0,
            "boost_phrases": len(_phrases),
            "lexicon_terms": len(_vocabulary)}


@app.get("/v1/models")
def models():
    # Same shape faster-whisper-server returns, so anything that probes for
    # liveness the way the bake-off does keeps working.
    return {"data": [{"id": MODEL_NAME, "object": "model",
                      "owned_by": "nvidia"}], "object": "list"}


@app.post("/v1/audio/transcriptions")
def transcriptions(file: UploadFile = File(...)):
    # Every other multipart field the client sends (model, language, prompt,
    # beam_size, temperature) is whisper vocabulary and is deliberately ignored:
    # the equivalent conditioning here is the boosting tree, which is decided at
    # load time from CONTACTS_PATH. Accepting and ignoring them is what lets one
    # client talk to either engine.
    try:
        wav_bytes = file.file.read()
    except Exception as exc:
        return JSONResponse({"error": "could not read upload: {}".format(exc)},
                            status_code=400)

    if not wav_bytes:
        return JSONResponse({"error": "empty audio"}, status_code=400)

    started = time.time()
    try:
        text, raw = transcribe_wav(wav_bytes)
    except Exception as exc:
        print("[stt] transcription failed: {}".format(exc))
        return JSONResponse({"error": str(exc)}, status_code=500)

    elapsed_ms = (time.time() - started) * 1000
    seconds = wav_seconds(wav_bytes)
    rtf = "" if not seconds else " rtf={:.2f}".format(elapsed_ms / 1000 / seconds)
    corrected = " (was: {})".format(raw) if raw != text else ""
    print("[stt] {:.0f}ms{} {}{}".format(elapsed_ms, rtf, text, corrected))

    return {"text": text}


if __name__ == "__main__":
    import uvicorn

    print("[stt] listening on http://{}:{}".format(HOST, PORT))
    # Loopback only — this is a local service and there is no auth on it.
    #
    # Address it by IP, never by "localhost": on Windows that name resolves to
    # ::1 first, and because this socket is IPv4-only every request then waits
    # ~2s for the IPv6 connect to be refused before falling back. Measured at
    # 2247ms per transcription via localhost against 205ms via 127.0.0.1 — with
    # identical 264ms of actual inference behind both.
    #
    # One worker: the model is loaded per process and holds a lock anyway.
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning")
