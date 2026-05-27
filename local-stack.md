# Local AI stack for L.A.I.T.H.

Four services replace the cloud APIs used by the `main` branch:

| Service | Replaces | Port | Endpoint |
|---|---|---|---|
| LM Studio | Gemini 2.5 Flash | 1234 | `http://localhost:1234/v1/chat/completions` |
| SearxNG | Gemini Google-search grounding | 8080 | `http://localhost:8080/search?q=...&format=json` |
| faster-whisper-server | Azure Speech-to-Text | 8000 | `http://localhost:8000/v1/audio/transcriptions` |
| Kokoro-FastAPI | Azure Neural TTS | 8880 | `http://localhost:8880/v1/audio/speech` |

LM Studio runs as a GUI process. The other three run in Docker via `docker-compose.yml`.

## Prerequisites

- **Docker Desktop** with the WSL 2 backend.
- **NVIDIA Container Toolkit** inside WSL 2 for GPU passthrough. Confirm with:
  `docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi`
- **LM Studio** installed on the host (lmstudio.ai).

## Start everything

```powershell
docker compose up -d   # faster-whisper, Kokoro, SearxNG

# Then launch LM Studio:
#   - Download model: Llama-3.1-8B-Instruct-GGUF (Q4_K_M)
#   - Developer tab → Start Server (port 1234)
```

First-run notes:
- **faster-whisper-server** pulls `Systran/faster-whisper-large-v3` (~3 GB) on the first transcription request. Allow ~2 min.
- **Kokoro-FastAPI** downloads voice models on first start.
- **SearxNG** uses `./searxng/settings.yml`. Replace `secret_key` with `openssl rand -hex 32` before exposing beyond localhost.

## Verify each service

```powershell
curl http://localhost:1234/v1/models                       # LM Studio
curl "http://localhost:8080/search?q=test&format=json"     # SearxNG
curl http://localhost:8000/v1/models                       # faster-whisper-server
curl http://localhost:8880/v1/audio/voices                 # Kokoro-FastAPI
```

All four must return JSON. If SearxNG returns HTML, `formats: [json]` in `searxng/settings.yml` wasn't picked up — restart the container.

## Stop

```powershell
docker compose down            # keep volumes (model caches preserved)
docker compose down -v         # also delete model caches
```

## GPU memory budget (6 GB target, e.g. RTX 4050)

| Service | Approx VRAM |
|---|---|
| LM Studio — Llama 3.1 8B Q4_K_M | ~5 GB |
| faster-whisper large-v3 (float16) | ~3 GB |
| Kokoro-FastAPI | ~1 GB |

Together they exceed 6 GB. Mitigations:
- Drop Llama to Q3_K_M or a 7B model.
- Switch faster-whisper to `WHISPER__COMPUTE_TYPE=int8_float16` (~1.5 GB) or `WHISPER__MODEL=Systran/faster-whisper-medium.en`.
- Run Kokoro on CPU: use `ghcr.io/remsky/kokoro-fastapi-cpu:latest` and drop the GPU `deploy` block.

## Logs

```powershell
docker compose logs -f faster-whisper-server
docker compose logs -f kokoro-fastapi
docker compose logs -f searxng
```
