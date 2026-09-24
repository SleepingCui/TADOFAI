

from __future__ import annotations

import asyncio

from .logging_setup import get_logger
from .models import TYPE_STATE
from .protocol import make_message, now

#: 状态没变化时的心跳间隔（秒）
HEARTBEAT_SECONDS = 1.0


async def state_broadcast_loop(services) -> None:
    log = get_logger("broadcast")
    last_payload: dict | None = None
    last_sent_at = 0.0

    log.info("状态广播任务已启动")
    try:
        while True:
            # 每轮都重新读配置：改 stateRate 立即生效
            rate = max(1, int(services.config_manager.get().server.state_rate))
            interval = 1.0 / rate

            payload = await services.state_store.as_wire()
            moment = now()
            changed = payload != last_payload
            needs_heartbeat = (moment - last_sent_at) >= HEARTBEAT_SECONDS

            if changed or needs_heartbeat:
                message = make_message(TYPE_STATE, payload, timestamp=moment)
                services.event_bus.publish_state(message)
                last_payload = payload
                last_sent_at = moment

            await asyncio.sleep(interval)
    except asyncio.CancelledError:
        log.info("状态广播任务已停止")
        raise
    except Exception:  # 广播循环不能因为一次异常就静默退出
        log.exception("状态广播异常退出")
        raise
