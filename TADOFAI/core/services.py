

from __future__ import annotations

import asyncio
import contextlib
from typing import Any

from fastapi import WebSocket

from .broadcaster import state_broadcast_loop
from .config import ConfigManager
from .event_bus import EventBus
from .health import HealthService
from .logging_setup import get_logger
from .models import PROTOCOL_VERSION, TYPE_HELLO, TYPE_STATE
from .plugin_manager import PluginManager
from .protocol import (
    SERIALIZER,
    ParsedMessage,
    ProtocolError,
    UnsupportedVersion,
    make_message,
    parse_message,
)
from .state_store import GameStateStore


class Services:
    def __init__(self, config_manager: ConfigManager) -> None:
        self.config_manager = config_manager
        self.config = config_manager.get()
        self.log = get_logger("services")

        self.event_bus = EventBus()
        self.state_store = GameStateStore()
        self.health = HealthService()
        self.plugin_manager = PluginManager(config_manager)

        self._broadcast_task: asyncio.Task | None = None
        self._mod_socket: WebSocket | None = None
        self._mod_session = 0
        self._client_sockets: set[WebSocket] = set()

    # --- 生命周期 ---------------------------------------------------------

    async def start(self) -> None:
        self.log.info(
            "TADOFAI Core 启动中（协议 v%d，序列化 %s，监听 %s:%d）",
            PROTOCOL_VERSION,
            SERIALIZER,
            self.config.server.host,
            self.config.server.port,
        )
        plugin_list = self.plugin_manager.scan()
        usable = [plugin for plugin in plugin_list if plugin.valid]
        for plugin in plugin_list:
            if plugin.valid:
                self.log.info("插件 %s v%s (%s) 已加载", plugin.id, plugin.version, plugin.type)
            else:
                self.log.error("插件 %s 不可用: %s", plugin.id, plugin.error)
        self.log.info("插件目录 %s：可用 %d / 共 %d", self.plugin_manager.resolve_directory(), len(usable), len(plugin_list))

        self._broadcast_task = asyncio.create_task(state_broadcast_loop(self), name="tadofai-broadcast")

    async def stop(self) -> None:
        if self._broadcast_task is not None:
            self._broadcast_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._broadcast_task
            self._broadcast_task = None

        await self.close_mod_socket()
        await self.close_client_sockets()
        self.config_manager.save()  # 关闭时保存配置
        self.log.info("TADOFAI Core 已停止")

    # --- Mod 会话 ---------------------------------------------------------

    async def begin_mod_session(self, socket: WebSocket) -> int:
        previous = self._mod_socket
        self._mod_session += 1
        session = self._mod_session
        self._mod_socket = socket
        self.health.note_mod_connected()
        self.log.info("Mod 已连接（session=%d）", session)

        if previous is not None and previous is not socket:
            # 只保留一个活跃 Mod 连接，避免两路数据互相覆盖
            self.log.warning("检测到新的 Mod 连接，关闭旧连接")
            await _safe_close(previous, code=1000, reason="replaced by new mod connection")
        return session

    async def end_mod_session(self, session: int) -> None:
        if session != self._mod_session:
            return  # 已被新连接顶掉，不在旧 session 上做断开处理

        self._mod_socket = None
        self.health.note_mod_disconnected()
        # §17：保留最后状态但标记 stale，等重连
        await self.state_store.mark_disconnected()
        self.log.info("Mod 已断开（session=%d），状态标记为 stale", session)

    async def close_mod_socket(self) -> None:
        socket, self._mod_socket = self._mod_socket, None
        if socket is not None:
            await _safe_close(socket, code=1001, reason="core shutting down")

    # --- 插件客户端 -------------------------------------------------------

    def track_client(self, socket: WebSocket) -> None:
        self._client_sockets.add(socket)

    def untrack_client(self, socket: WebSocket) -> None:
        self._client_sockets.discard(socket)

    async def close_client_sockets(self) -> None:
        sockets = list(self._client_sockets)
        self._client_sockets.clear()
        for socket in sockets:
            await _safe_close(socket, code=1001, reason="core shutting down")

    # --- Mod 消息 ---------------------------------------------------------

    async def handle_mod_frame(self, raw: str | bytes) -> None:
        """解析一帧 Mod 数据；任何格式问题都只记一次并继续接收。"""
        try:
            parsed = parse_message(raw)
        except UnsupportedVersion as exc:
            self.health.note_rejected(str(exc))
            self.log.warning("拒绝 Mod 消息（协议版本）: %s", exc)
            return
        except ProtocolError as exc:
            self.health.note_rejected(str(exc))
            self.log.warning("拒绝 Mod 消息: %s", exc)
            return

        self.health.note_message()

        if not parsed.known:
            self.health.note_unknown_type(parsed.type)
            # 只告警一次，避免未知类型刷屏
            if self.health.unknown_types[parsed.type] == 1:
                self.log.warning("未知消息类型（已原样转发给插件）: %s", parsed.type)

        try:
            await self.handle_mod_message(parsed)
        except Exception:
            self.log.exception("处理 Mod 消息失败: %s", parsed.type)

    async def handle_mod_message(self, parsed: ParsedMessage) -> None:
        if parsed.type == TYPE_HELLO:
            self.health.note_hello(parsed.data)
            self.log.info(
                "Mod 握手: client=%s mod=%s game=%s capabilities=%s",
                self.health.mod_client or "?",
                self.health.mod_version or "?",
                self.health.game_version or "?",
                ",".join(self.health.capabilities) or "-",
            )
        elif parsed.type == TYPE_STATE:
            # 状态整体覆盖；具体广播由 broadcaster 按 stateRate 做
            await self.state_store.apply_mod_state(parsed.data)
            return

        # 其余（含 hello 与未知类型）按事件转发，id 原样带给插件用于检测丢包
        message = make_message(parsed.type, parsed.data, message_id=parsed.id)
        delivered = self.event_bus.publish_event(message)
        self.log.debug("事件 %s -> %d 个客户端", parsed.type, delivered)

    # --- 统计 -------------------------------------------------------------

    async def health_snapshot(self) -> dict[str, Any]:
        dropped_by_mod = await self.state_store.dropped_events()
        return self.health.snapshot(
            clientCount=self.event_bus.client_count,
            eventQueueSize=self.event_bus.total_pending_events,
            maxClientQueueSize=self.event_bus.max_pending_events,
            # Mod 上报的丢包（队列满时丢最旧）
            droppedEvents=dropped_by_mod,
            # Core 侧因慢客户端丢的事件
            clientDroppedEvents=self.event_bus.total_dropped_events,
            serializer=SERIALIZER,
            stateUpdates=self.state_store.update_count,
        )


async def _safe_close(socket: WebSocket, *, code: int = 1000, reason: str = "") -> None:
    """关闭可能已经断开的 WebSocket，不抛异常。"""
    try:
        await socket.close(code=code, reason=reason[:120])
    except Exception:
        pass
