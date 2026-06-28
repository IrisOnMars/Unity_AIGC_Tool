# com.aigc.toolchain

Editor-only Unity package for AIGC asset generation.
It expands a short description with Qwen and submits the final prompt to ComfyUI, then imports the generated PNG into Unity.

## Features

- Unity Editor window for one-click generation
- Optional auto-start of local Qwen OpenAI-compatible server
- Prompt extension pipeline (supports CJK source text)
- ComfyUI submission, polling, and output download
- Auto import generated image into Assets/AIGC_Generated

## Package Info

- Name: com.aigc.toolchain
- Unity: 2021.3+
- Menu: AIGC Tool/Asset Generator

## Folder Layout

- Editor/: Unity editor scripts and ComfyUI workflow template
- Tools/: local Qwen server script

## Requirements

- Unity 2021.3 or newer
- Running ComfyUI service (default: http://127.0.0.1:8188)
- Python environment for local Qwen server (if Auto Start Qwen is enabled)
- Hugging Face transformers + torch for the local server script

## Quick Start

1. Open Unity, then open menu: AIGC Tool/Asset Generator.
2. In Settings, confirm:
   - Qwen Endpoint (default: http://127.0.0.1:8000/v1/chat/completions)
   - ComfyUI Base URL (default: http://127.0.0.1:8188)
   - Auto Start Qwen and Python/Script paths if using local server startup
3. Enter Source Description and click Generate Asset.
4. Generated image will be saved and imported under Assets/AIGC_Generated.

## Local Qwen Server Notes

The script at Tools/qwen_openai_server.py starts an OpenAI-compatible endpoint at /v1/chat/completions.
Before first run, update model path and runtime environment to your machine.

Current defaults in the script include:

- MODEL_PATH: local absolute path
- HOST: 127.0.0.1
- PORT: 8000
- DEVICE from env QWEN_DEVICE (default: cuda)

Recommended environment variables:

- QWEN_DEVICE
- QWEN_OUT_LOG
- QWEN_ERR_LOG

## ComfyUI Workflow Template

The file Editor/single_image_api.json must contain placeholder __PROMPT__ in the positive prompt text.
The package replaces it with the expanded prompt before sending to ComfyUI /prompt.


