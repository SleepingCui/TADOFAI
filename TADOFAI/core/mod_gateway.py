
from __future__ import annotations

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from .logging_setup import get_logger


def register_mod_gateway(app: FastAPI, services) -> None:
    log = get_logger("mod")

    @app.websocket("/ws/mod")
    async def mod_socket(websocket: WebSocket) -> None:
        await websocket.accept()
        session = await services.begin_mod_session(websocket)

        try:
            while True:
                frame = await websocket.receive()
                if frame.get("type") == "websocket.disconnect":
                    break

                raw = frame.get("text")
                if raw is None:
                    raw = frame.get("bytes")
                if raw is None:
                    continue

                await services.handle_mod_frame(raw)
        except WebSocketDisconnect:
            pass
        except Exception:
            log.exception("Mod 连接处理异常")
        finally:
            await services.end_mod_session(session)
