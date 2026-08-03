# Runs the Parakeet STT service on the host instead of in Docker.
#
# The normal path is `docker compose up -d stt`. This is for debugging against
# the host Python, or for the GPU build (-Gpu), which the image doesn't carry a
# CUDA runtime for.
#
#   .\stt-server\start.ps1              # foreground, :8001
#   .\stt-server\start.ps1 -Port 8002   # somewhere else
#   .\stt-server\start.ps1 -Gpu         # fp16 on CUDA (~20ms faster, 1.3 GB)

param(
    [int]$Port = 8001,
    [switch]$Gpu
)

$ErrorActionPreference = "Stop"

$env:PARAKEET_PORT = "$Port"
if ($Gpu) {
    $env:PARAKEET_DEVICE = "cuda"
    $env:PARAKEET_HALF = "1"
}

if (-not $env:CONTACTS_PATH -or -not (Test-Path $env:CONTACTS_PATH)) {
    # Word boosting toward contact names is most of why this engine was chosen;
    # without the file it degrades to an ordinary Parakeet and name recall drops
    # from 75% to 41.7%.
    Write-Warning "CONTACTS_PATH is not set to a readable file - contact-name boosting will be off."
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
python "$here\parakeet_server.py"
