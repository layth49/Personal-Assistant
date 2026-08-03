# Local AI stack for L.A.I.T.H.

Four services replace the cloud APIs used by the `main` branch:

| Service | Replaces | Port | Endpoint |
|---|---|---|---|
| LM Studio — `qwen/qwen3-4b-2507` | Gemini 2.5 Flash | 1234 | `http://127.0.0.1:1234/v1/chat/completions` |
| SearxNG | Gemini Google-search grounding | 8080 | `http://localhost:8080/search?q=...&format=json` |
| Parakeet STT (`stt-server/`) | Azure Speech-to-Text | 8001 | `http://127.0.0.1:8001/v1/audio/transcriptions` |
| Kokoro-FastAPI | Azure Neural TTS | 8880 | `http://localhost:8880/v1/audio/speech` |

Both model choices were settled by bake-off rather than by reputation —
`bakeoff/llm/README.md` and `bakeoff/stt/README.md` have the tables.

SearxNG, Kokoro and Parakeet run in Docker; LM Studio is a GUI process. The STT
image is built from `stt-server/` rather than pulled, and is CPU-only on purpose
(`stt-server/README.md`).

faster-whisper-server is still in `docker-compose.yml` but behind a profile: it
lost the bake-off and its ~2.2 GB of VRAM is better spent on the LLM.

## Prerequisites

- **Docker Desktop** with the WSL 2 backend.
- **NVIDIA Container Toolkit** inside WSL 2 for GPU passthrough. Confirm with:
  `docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi`
- **LM Studio** installed on the host (lmstudio.ai).
- **`.env`** in the repo root with `CONTACTS_PATH` pointing at your contacts JSON
  — copy `.env.example`. Compose mounts that file into the STT container.
  (Python 3.12 with `stt-server/requirements.txt` is only needed if you run the
  STT service on the host instead of in Docker.)

## Start everything

```powershell
docker compose up -d          # SearxNG + Kokoro + Parakeet STT

# Then LM Studio:
#   - Model: qwen/qwen3-4b-2507
#   - Context >= 8K (the ~30 tool schemas are a big prompt)
#   - Developer tab -> Start Server (port 1234)
```

The app omits the `model` field on chat calls, so LM Studio serves whatever is
loaded — swapping the LLM is config, not code.

First-run notes:
- **Parakeet** builds its image locally (torch CPU + NeMo, several minutes) and
  then pulls `nvidia/parakeet-tdt-0.6b-v2` (~2.4 GB) into the `parakeet-models`
  volume on first start. After that it loads in ~6 s and the volume survives
  rebuilds.
- **Kokoro-FastAPI** downloads voice models on first start.
- **SearxNG** uses `./searxng/settings.yml`. Replace `secret_key` with
  `openssl rand -hex 32` before exposing beyond localhost.

## Verify each service

```powershell
curl http://127.0.0.1:1234/v1/models                       # LM Studio
curl "http://localhost:8080/search?q=test&format=json"     # SearxNG
curl http://127.0.0.1:8001/health                          # Parakeet STT
curl http://localhost:8880/v1/audio/voices                 # Kokoro-FastAPI
```

All four must return JSON. If SearxNG returns HTML, `formats: [json]` in
`searxng/settings.yml` wasn't picked up — restart the container.

On the STT line, check `boost_phrases` is not 0. Zero means `CONTACTS_PATH`
didn't resolve and contact-name recall has dropped from 75% to 41.7%.

**Use `127.0.0.1` for the STT service, not `localhost`.** Docker publishes
dual-stack so the container is fine either way, but the host build (`start.ps1`)
binds IPv4 and `localhost` resolves to `::1` first on Windows — every request
then pays ~2 s waiting for the IPv6 connect to fail. See `stt-server/README.md`.

## Stop

```powershell
docker compose down            # keep volumes (model caches preserved)
docker compose down -v         # also delete model caches
```

## GPU memory budget (6 GB target, RTX 4050)

Measured on this machine, whole-card, with a desktop session running:

| State | VRAM |
|---|---|
| Idle, LM Studio + Kokoro loaded, whisper stopped | 2054 MiB |
| Peak with STT + LLM + TTS all firing at once | **5175 MiB** of 6141 |
| Same, with the whisper container also running | +2219 MiB — does not fit |

Parakeet contributes nothing to this: it runs on the CPU (~2.5 GB of container
RAM, ~230 ms per utterance, against the GPU build's ~255 ms — the CPU is both
cheaper and, here, no slower).

If VRAM ever has to be reclaimed:
- Drop the LLM to a smaller quant, or to `lfm2.5-1.2b` (730 MB, but it misroutes
  one request in three — see the LLM bake-off).
- Run Kokoro on CPU: `ghcr.io/remsky/kokoro-fastapi-cpu:latest`, drop the GPU
  `deploy` block. Costs ~2 s of time-to-first-audio, so it is a last resort.
- Close the Claude desktop app before measuring anything. Idle, it was the
  largest single GPU consumer on this machine (35.5% of a 19–41% baseline).

## Logs

```powershell
docker compose logs -f stt        # one line per utterance: latency, RTF, text
docker compose logs -f kokoro
docker compose logs -f searxng
```
