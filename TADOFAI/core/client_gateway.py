

from __future__ import annotations

import asyncio

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from .event_bus import Subscriber
from .logging_setup import get_logger
from .models import TYPE_STATE
from .protocol import ProtocolError, dumps, make_message, parse_subscribe

#: 同时连接的插件客户端上限
MAX_CLIENTS = 32
#: 没有数据时的轮询间隔（秒）
SEND_INTERVAL = 0.002
#: 单轮最多连发的事件数，避免状态帧被事件流饿死
EVENT_BATCH = 256


def register_client_gateway(app: FastAPI, services) -> None:
    log = get_logger("client")

    @app.websocket("/ws/client")
    async def client_socket(websocket: WebSocket) -> None:
        if services.event_bus.client_count >= MAX_CLIENTS:
            log.warning("客户端数已达上限 %d，拒绝新连接", MAX_CLIENTS)
            await websocket.close(code=1013)  # try again later
            return

        await websocket.accept()
        # 默认先订阅状态：即使客户端不发 subscribe，也能立刻看到画面
        subscriber = services.event_bus.subscribe(want_state=True, events=())
        services.track_client(websocket)

        sender = asyncio.create_task(
            _send_loop(websocket, subscriber, log),
            name=f"tadofai-client-{subscriber.id}",
        )
        log.info("插件客户端 %d 已连接（当前 %d 个）", subscriber.id, services.event_bus.client_count)
        await _push_state(websocket, services, subscriber)

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

                try:
                    parsed = parse_subscribe(raw)
                except ProtocolError as exc:
                    # 一条坏订阅不影响连接，继续等正确的
                    log.warning("客户端 %d 的订阅消息非法: %s", subscriber.id, exc)
                    continue

                services.event_bus.update_subscription(
                    subscriber,
                    want_state=bool(parsed.data.get("state", True)),
                    events=parsed.data.get("events") or (),
                )
                log.debug(
                    "客户端 %d 订阅: state=%s events=%s",
                    subscriber.id,
                    subscriber.want_state,
                    ",".join(sorted(subscriber.events)) or "-",
                )
                # 订阅完立刻补一份完整状态，页面刷新后不用等下一个状态变化
                if subscriber.want_state:
                    await _push_state(websocket, services, subscriber)
        except WebSocketDisconnect:
            pass
        except Exception:
            log.exception("插件客户端 %d 处理异常", subscriber.id)
        finally:
            sender.cancel()
            services.event_bus.unsubscribe(subscriber)
            services.untrack_client(websocket)
            log.info("插件客户端 %d 已断开", subscriber.id)


async def _push_state(websocket: WebSocket, services, subscriber: Subscriber) -> None:
    """把当前完整状态放进订阅者的状态槽（由发送任务真正写出去）。"""
    message = make_message(TYPE_STATE, await services.state_store.as_wire())
    subscriber.state_slot = message


async def _send_loop(websocket: WebSocket, subscriber: Subscriber, log) -> None:
    try:
        while True:
            sent = False

            state = subscriber.take_state()
            if state is not None:
                await websocket.send_text(dumps(state))
                subscriber.sent += 1
                sent = True

            for _ in range(EVENT_BATCH):
                event = subscriber.take_event()
                if event is None:
                    break
                await websocket.send_text(dumps(event))
                subscriber.sent += 1
                sent = True

            if not sent:
                await asyncio.sleep(SEND_INTERVAL)
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        # 发送失败说明连接已经坏了：关掉 socket 让接收循环结束，由 finally 收尾
        log.debug("客户端 %d 发送结束: %s", subscriber.id, exc)
        try:
            await websocket.close()
        except Exception:
            pass
