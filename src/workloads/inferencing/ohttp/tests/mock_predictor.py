"""Mock predictor server for OHTTP integration tests.

Serves canned responses for KServe v2 and OpenAI-compatible chat endpoints
over HTTPS using a provided TLS certificate.
"""

import argparse
import ssl
import time

from flask import Flask, jsonify, request

app = Flask(__name__)


@app.route("/v2/models/<model_name>/infer", methods=["POST"])
def v2_infer(model_name):
    """KServe v2 inference endpoint — returns a canned classification result."""
    return jsonify(
        {
            "model_name": model_name,
            "outputs": [
                {
                    "name": "predict",
                    "shape": [2],
                    "datatype": "INT64",
                    "data": [1, 1],
                }
            ],
        }
    )


@app.route("/v1/chat/completions", methods=["POST"])
def chat_completions():
    """OpenAI-compatible chat completions — returns a canned response.

    Generates a moderately sized response body to exercise chunked streaming
    through the OHTTP gateway.
    """
    body = request.get_json(silent=True) or {}
    model = body.get("model", "mock-model")

    # Build a response large enough to exercise streaming (>4KB).
    content = (
        "The quick brown fox jumps over the lazy dog. " * 50
        + "Mock inference complete."
    )

    return jsonify(
        {
            "id": "mock-completion-001",
            "object": "chat.completion",
            "created": int(time.time()),
            "model": model,
            "choices": [
                {
                    "index": 0,
                    "message": {"role": "assistant", "content": content},
                    "finish_reason": "stop",
                }
            ],
            "usage": {
                "prompt_tokens": 10,
                "completion_tokens": len(content.split()),
                "total_tokens": 10 + len(content.split()),
            },
        }
    )


@app.route("/ready", methods=["GET"])
def ready():
    """Health check endpoint."""
    return jsonify({"status": "ready"})


def main():
    parser = argparse.ArgumentParser(description="Mock predictor HTTPS server")
    parser.add_argument("--cert", required=True, help="Path to TLS certificate file.")
    parser.add_argument("--key", required=True, help="Path to TLS private key file.")
    parser.add_argument("--port", type=int, default=8443, help="Listen port.")
    args = parser.parse_args()

    ssl_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ssl_ctx.load_cert_chain(args.cert, args.key)

    print(f"Mock predictor starting on https://0.0.0.0:{args.port}")
    app.run(host="0.0.0.0", port=args.port, ssl_context=ssl_ctx)


if __name__ == "__main__":
    main()
