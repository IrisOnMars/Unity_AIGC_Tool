import json
import os
import sys
import tempfile
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import torch
from transformers import AutoModelForImageTextToText, AutoProcessor


MODEL_PATH = r"F:\py_project\qwen3\models\Qwen3-VL-2B-Instruct"
MODEL_ID = "Qwen3-VL-2B-Instruct"
HOST = "127.0.0.1"
PORT = 8000
OUT_LOG = os.environ.get("QWEN_OUT_LOG") or os.path.join(tempfile.gettempdir(), "qwen_openai_server.out.log")
ERR_LOG = os.environ.get("QWEN_ERR_LOG") or os.path.join(tempfile.gettempdir(), "qwen_openai_server.err.log")
DEVICE = os.environ.get("QWEN_DEVICE", "cuda").strip().lower()

sys.stdout = open(OUT_LOG, "a", encoding="utf-8", buffering=1)
sys.stderr = open(ERR_LOG, "a", encoding="utf-8", buffering=1)


print("Loading Qwen processor...", flush=True)
processor = AutoProcessor.from_pretrained(MODEL_PATH, trust_remote_code=True, local_files_only=True)

print(f"Loading Qwen model on {DEVICE}...", flush=True)
if DEVICE in ("cuda", "gpu") and torch.cuda.is_available():
    model = AutoModelForImageTextToText.from_pretrained(
        MODEL_PATH,
        dtype=torch.bfloat16,
        device_map="auto",
        trust_remote_code=True,
        local_files_only=True,
    )
else:
    DEVICE = "cpu"
    model = AutoModelForImageTextToText.from_pretrained(
        MODEL_PATH,
        dtype=torch.float32,
        device_map="cpu",
        trust_remote_code=True,
        local_files_only=True,
    )
model.eval()


def send_json(handler, status, payload):
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)


def normalize_content(content):
    if isinstance(content, str):
        return content

    if isinstance(content, list):
        parts = []
        for item in content:
            if isinstance(item, dict):
                if item.get("type") == "text" and "text" in item:
                    parts.append(str(item["text"]))
                elif "text" in item:
                    parts.append(str(item["text"]))
            else:
                parts.append(str(item))
        return "\n".join(parts)

    return str(content) if content is not None else ""


def cleanup_output(text):
    text = (text or "").strip()
    lower = text.lower()
    if "</think>" in lower:
        index = lower.rfind("</think>")
        text = text[index + len("</think>") :].strip()

    for prefix in ("Prompt:", "Final prompt:", "Positive prompt:"):
        if text.lower().startswith(prefix.lower()):
            text = text[len(prefix) :].strip()

    return " ".join(text.replace("\r", " ").replace("\n", " ").split())


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        print("%s - %s" % (self.address_string(), fmt % args), flush=True)

    def do_GET(self):
        if self.path.rstrip("/") == "/v1/models":
            send_json(self, 200, {"object": "list", "data": [{"id": MODEL_ID, "object": "model"}]})
            return

        send_json(self, 404, {"error": {"message": "Not found"}})

    def do_POST(self):
        if self.path.rstrip("/") != "/v1/chat/completions":
            send_json(self, 404, {"error": {"message": "Not found"}})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            request = json.loads(self.rfile.read(length).decode("utf-8"))
            messages = request.get("messages") or []

            normalized = []
            for message in messages:
                if not isinstance(message, dict):
                    continue
                normalized.append(
                    {
                        "role": message.get("role", "user"),
                        "content": normalize_content(message.get("content", "")),
                    }
                )

            if not normalized:
                raise ValueError("messages is required")

            max_tokens = int(request.get("max_tokens") or 260)
            max_tokens = max(32, min(max_tokens, 512))
            temperature = float(request.get("temperature") if request.get("temperature") is not None else 0.25)
            do_sample = temperature > 0.05

            prompt = processor.apply_chat_template(normalized, tokenize=False, add_generation_prompt=True)
            inputs = processor(text=[prompt], return_tensors="pt").to(model.device)
            generate_kwargs = {
                "max_new_tokens": max_tokens,
                "repetition_penalty": 1.08,
                "do_sample": do_sample,
            }

            if do_sample:
                generate_kwargs["temperature"] = max(0.1, temperature)
                generate_kwargs["top_p"] = float(request.get("top_p") or 0.8)

            with torch.no_grad():
                generated_ids = model.generate(**inputs, **generate_kwargs)

            output_ids = generated_ids[0, inputs.input_ids.shape[-1] :]
            content = cleanup_output(processor.decode(output_ids, skip_special_tokens=True))

            send_json(
                self,
                200,
                {
                    "id": "chatcmpl-local-qwen",
                    "object": "chat.completion",
                    "model": MODEL_ID,
                    "choices": [
                        {
                            "index": 0,
                            "message": {"role": "assistant", "content": content},
                            "finish_reason": "stop",
                        }
                    ],
                },
            )
        except Exception as exc:
            traceback.print_exc()
            send_json(self, 500, {"error": {"message": str(exc)}})


if __name__ == "__main__":
    try:
        server = ThreadingHTTPServer((HOST, PORT), Handler)
        print(f"Qwen OpenAI-compatible service ready at http://{HOST}:{PORT}/v1/chat/completions", flush=True)
        server.serve_forever()
    except BaseException:
        traceback.print_exc()
        sys.stderr.flush()
        raise
