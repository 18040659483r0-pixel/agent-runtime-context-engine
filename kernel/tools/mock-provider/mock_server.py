#!/usr/bin/env python3
"""OpenAI 兼容 mock provider —— 开发/离线验证用，**不属于 Runtime 组成部分**。

用途：在没有真实 API Key、或不想花钱/受网络影响时，验证
`Runtime → API → Model → Runtime` 这条闭环真的跑通（也供 Benchmark 做确定性对照）。

启动：
    python3 tools/mock-provider/mock_server.py            # 默认 127.0.0.1:8899
    MOCK_PORT=9000 python3 tools/mock-provider/mock_server.py

配套配置：src/AgentRuntime.Cli/config.mock.json（baseUrl=http://127.0.0.1:8899/v1）

行为：
- POST /v1/chat/completions  → 返回固定的 OpenAI 兼容响应（带 usage，含缓存字段）
- 回显收到的第一条 user 消息，便于确认「请求真的到了」
- 任何其它路径 → 404；非 POST → 405
"""
from __future__ import annotations

import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = os.environ.get("MOCK_HOST", "127.0.0.1")
PORT = int(os.environ.get("MOCK_PORT", "8899"))
PATH = "/v1/chat/completions"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self) -> None:  # noqa: N802 (stdlib naming)
        if self.path.rstrip("/") != PATH:
            self._json(404, {"error": {"message": f"unknown path: {self.path}", "type": "not_found"}})
            return

        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length).decode("utf-8") if length else "{}"
        try:
            request = json.loads(raw)
        except json.JSONDecodeError as exc:
            self._json(400, {"error": {"message": f"bad json: {exc}", "type": "invalid_request_error"}})
            return

        messages = request.get("messages") or []
        first_user = next((m.get("content", "") for m in messages if m.get("role") == "user"), "")
        model = request.get("model", "mock-model")

        # 工具面 E2E 专用分支（不影响下面那条固定回显）：
        #   用户消息以 "@tool " 开头 ⇒ 回一段**行首**的 [TOOL] 块（真实模型回复的等价物）。
        #   用法：@tool write {"path":"/tmp/x.txt","content":"hi"} —— 便于在 TUI 里敲不出来换行时也能驱动一次工具调用。
        reply = f"[mock] 收到：{first_user}"

        # 取**最后一条**以 "@tool " 开头的 user 消息（多轮时不能用"第一条"，否则会拿旧目标）。
        # 回的是**带 [TOOL] 块头**的一行 —— 协议要求块头在行首，这里正好是一行的开头。
        tool_payload = next(
            (m["content"][len("@tool "):].strip()
             for m in reversed(messages)
             if m.get("role") == "user"
             and isinstance(m.get("content"), str)
             and m["content"].startswith("@tool ")),
            None,
        )
        if tool_payload is not None:
            reply = "[TOOL] " + tool_payload

        # 固定回显 + 固定 usage：让 Benchmark 的 T01/T02/T03 有确定性基线。
        self._json(200, {
            "id": "chatcmpl-mock-0001",
            "object": "chat.completion",
            "created": 1757000000,
            "model": model,
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": reply},
                "finish_reason": "stop",
            }],
            "usage": {
                "prompt_tokens": 12,
                "completion_tokens": 8,
                "total_tokens": 20,
                "prompt_tokens_details": {"cached_tokens": 0},
            },
        })

    def do_GET(self) -> None:  # noqa: N802
        self._json(405, {"error": {"message": "use POST /v1/chat/completions", "type": "method_not_allowed"}})

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("[mock] " + (fmt % args) + "\n")


if __name__ == "__main__":
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    sys.stderr.write(f"[mock] listening on http://{HOST}:{PORT}{PATH}\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        sys.stderr.write("[mock] bye\n")
