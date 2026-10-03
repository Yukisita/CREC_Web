"""Run the real Web API/MCP/HTTP stack against a local deterministic LLM stub.

Run with CREC_MCPServer/.venv/Scripts/python.exe tests/chat_integration.py after
installing requirements.txt and building tests/Chat.Tests.
"""

import json
import logging
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parent.parent
OUTPUTS = {
    "explain": "This is a collection manager.",
    "plan": 'I will search.<action>{"type":"fillInput","id":"searchText","value":"カメラ"}</action>'
            '<action>{"type":"clickButton","id":"searchButton"}</action>',
    "invalid-plan": '<action>{"type":"fillInput","id":"unknown","value":"bad"}</action>'
                    '<action>{"type":"clickButton","id":"saveIndexEdit"}</action>',
    "delete": '<action>{"type":"clickButton","id":"deleteCollectionBtn"}</action>',
}


class LlmStub(BaseHTTPRequestHandler):
    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        message = payload["messages"][-1]["content"]
        result = {"choices": [{"message": {"content": OUTPUTS[message]}}]}
        body = json.dumps(result).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def main():
    import uvicorn

    with ThreadingHTTPServer(("127.0.0.1", 0), LlmStub) as llm:
        llm_thread = threading.Thread(target=llm.serve_forever, daemon=True)
        llm_thread.start()
        try:
            with socket.socket() as port_socket:
                port_socket.bind(("127.0.0.1", 0))
                mcp_port = port_socket.getsockname()[1]
            with tempfile.TemporaryDirectory(prefix="crec-chat-test-") as directory:
                os.environ.update(MCP_HOST="127.0.0.1", MCP_PORT=str(mcp_port),
                                  LLM_URL=f"http://127.0.0.1:{llm.server_port}",
                                  LLM_TIMEOUT="5", CHAT_LOG_DIR=directory)
                os.environ.pop("SAFE_BUTTON_IDS", None)
                os.environ.pop("SAFE_INPUT_IDS", None)
                sys.path.insert(0, str(ROOT / "CREC_MCPServer"))
                from server import mcp, chat_log

                # Run the production MCP app in this process so Windows virtual-env
                # launchers cannot leave an orphaned server process holding log files.
                server = uvicorn.Server(uvicorn.Config(
                    mcp.streamable_http_app(), host="127.0.0.1", port=mcp_port,
                    log_level="error",
                ))
                server_thread = threading.Thread(target=server.run, daemon=True)
                server_thread.start()
                try:
                    deadline = time.monotonic() + 15
                    while not server.started:
                        if not server_thread.is_alive() or time.monotonic() >= deadline:
                            raise RuntimeError("MCP server failed to start")
                        time.sleep(0.1)
                    result = subprocess.run(
                        ["dotnet", "run", "--project", "tests/Chat.Tests", "--no-build", "--",
                         "--mcp-url", f"http://127.0.0.1:{mcp_port}"], cwd=ROOT, timeout=45,
                    )
                    if result.returncode:
                        raise RuntimeError("Chat integration test failed")
                finally:
                    server.should_exit = True
                    server_thread.join(timeout=10)
                    chat_log.close()
                    logging.shutdown()
                    if server_thread.is_alive():
                        raise RuntimeError("MCP server did not shut down")
        finally:
            llm.shutdown()
            llm_thread.join(timeout=5)


if __name__ == "__main__":
    main()
