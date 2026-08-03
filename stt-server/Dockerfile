# Parakeet STT service.
#
# CPU-only on purpose. The bake-off measured the GPU build at ~255ms against the
# CPU's ~270ms — 15ms is not worth 1.3 GB of a 6 GB card that the LLM and Kokoro
# are already competing over. So this image installs the CPU torch wheel, which
# also keeps it a few GB smaller than a CUDA one.
FROM python:3.12-slim

# libsndfile is what soundfile/lhotse read WAVs through; ffmpeg covers anything
# that isn't plain PCM.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libsndfile1 ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# CPU wheel explicitly — the default index would drag in the whole CUDA runtime
# for a container that will never touch the GPU.
RUN pip install --no-cache-dir torch --index-url https://download.pytorch.org/whl/cpu

# nemo_toolkit is here for one feature: word boosting fused into TDT decoding.
# The much lighter onnx-asr build of the same model cannot reach it, and it is
# worth 33 points of contact-name recall. See README.md.
RUN pip install --no-cache-dir \
        "nemo_toolkit[asr]>=2.7" \
        "fastapi>=0.115" \
        "uvicorn>=0.30" \
        "python-multipart>=0.0.9"

WORKDIR /app
COPY parakeet_server.py lexicon.py ./

# Model weights land here; docker-compose mounts a volume so the ~2.4 GB
# download survives a rebuild.
ENV HF_HOME=/models

# Inside a container the loopback default would make the service unreachable —
# the published port has to reach it from outside the network namespace.
ENV PARAKEET_HOST=0.0.0.0

EXPOSE 8001

HEALTHCHECK --interval=30s --timeout=5s --start-period=300s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8001/health', timeout=4).status == 200 else 1)"

CMD ["python", "parakeet_server.py"]
