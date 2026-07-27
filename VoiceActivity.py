"""Silero VAD, driven frame by frame for the always-on listener.

The C# side (STTClient.ContinuousListener) owns the microphone and the
onset/endpoint state machine; this module answers one question — "how much
speech is in these samples?" — and keeps the model's recurrent state between
calls so it works on a live stream rather than a whole recording.

No new binary dependency: the app already embeds Python via pythonnet, and both
onnxruntime and the Silero model ship with faster-whisper, which the local stack
already uses for transcription.
"""
import os

import numpy as np
import onnxruntime

# Silero v6 as packaged by faster-whisper: 512-sample frames at 16 kHz, each fed
# with the previous frame's trailing 64 samples as context, and LSTM h/c carried
# across calls.
SAMPLE_RATE = 16000
FRAME_SAMPLES = 512
CONTEXT_SAMPLES = 64


def _model_path():
    override = os.environ.get("SILERO_VAD_PATH")
    if override and os.path.exists(override):
        return override
    try:
        import faster_whisper
        candidate = os.path.join(
            os.path.dirname(faster_whisper.__file__), "assets", "silero_vad_v6.onnx")
        if os.path.exists(candidate):
            return candidate
    except Exception:
        pass
    here = os.path.dirname(os.path.abspath(__file__))
    local = os.path.join(here, "silero_vad_v6.onnx")
    if os.path.exists(local):
        return local
    raise FileNotFoundError(
        "silero_vad_v6.onnx not found — set SILERO_VAD_PATH or install faster-whisper")


def _coerce_bytes(data):
    """Accept whatever the caller can cheaply hand us.

    pythonnet marshals a C# byte[] as a .NET array proxy rather than Python
    bytes, so it arrives as a plain sequence; base64 is accepted too as an
    escape hatch if that proxy ever turns out to be the expensive path.
    """
    if isinstance(data, (bytes, bytearray, memoryview)):
        return bytes(data)
    if isinstance(data, str):
        import base64
        return base64.b64decode(data)
    return bytes(bytearray(data))


class _Vad:
    def __init__(self):
        opts = onnxruntime.SessionOptions()
        # Single-threaded: this runs ~31 times a second on tiny inputs, so thread
        # pools cost more than they save and would fight the rest of the stack.
        opts.inter_op_num_threads = 1
        opts.intra_op_num_threads = 1
        opts.enable_cpu_mem_arena = False
        opts.log_severity_level = 4
        self.session = onnxruntime.InferenceSession(
            _model_path(), providers=["CPUExecutionProvider"], sess_options=opts)
        self.reset()

    def reset(self):
        self.h = np.zeros((1, 1, 128), dtype=np.float32)
        self.c = np.zeros((1, 1, 128), dtype=np.float32)
        self.context = np.zeros(CONTEXT_SAMPLES, dtype=np.float32)
        self.pending = np.zeros(0, dtype=np.float32)

    def push(self, pcm_bytes):
        """Feed 16-bit mono PCM. Returns the highest speech probability among the
        frames this completed, or -1.0 if there wasn't a whole frame yet."""
        if pcm_bytes is None:
            return -1.0
        raw = _coerce_bytes(pcm_bytes)
        if not raw:
            return -1.0

        samples = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        if self.pending.size:
            samples = np.concatenate([self.pending, samples])

        n_frames = samples.size // FRAME_SAMPLES
        if n_frames == 0:
            self.pending = samples
            return -1.0

        used = n_frames * FRAME_SAMPLES
        self.pending = samples[used:].copy()

        best = 0.0
        for i in range(n_frames):
            frame = samples[i * FRAME_SAMPLES:(i + 1) * FRAME_SAMPLES]
            inp = np.concatenate([self.context, frame]).reshape(1, -1).astype(np.float32)
            out, self.h, self.c = self.session.run(
                None, {"input": inp, "h": self.h, "c": self.c})
            self.context = frame[-CONTEXT_SAMPLES:].copy()
            prob = float(np.asarray(out).reshape(-1)[0])
            if prob > best:
                best = prob
        return best


_vad = None


def load():
    """Build the session up front so the first real frame isn't slowed by it."""
    global _vad
    if _vad is None:
        _vad = _Vad()
    return True


def reset():
    """Clear recurrent state — call when a new listening session starts, so the
    previous utterance's tail can't colour the next one."""
    if _vad is not None:
        _vad.reset()


def push(pcm_bytes):
    if _vad is None:
        load()
    return _vad.push(pcm_bytes)
