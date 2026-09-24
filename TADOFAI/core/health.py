
from __future__ import annotations

import time
from collections import Counter
from dataclasses import dataclass, field
from typing import Any

from .models import PROTOCOL_VERSION


@dataclass
class HealthService:
    started_at: float = field(default_factory=time.time)
    protocol_version: int = PROTOCOL_VERSION

    mod_connected: bool = False
    mod_connects: int = 0
    mod_disconnects: int = 0
    mod_messages: int = 0
    rejected_messages: int = 0
    last_mod_message_at: float | None = None
    last_reject_reason: str = ""

    #: 最近一次 hello 声明的身份
    mod_client: str = ""
    mod_version: str = ""
    game_version: str = ""
    capabilities: list[str] = field(default_factory=list)

    #: 未知消息类型统计，用来发现协议漂移
    unknown_types: Counter = field(default_factory=Counter)

    def note_mod_connected(self) -> None:
        self.mod_connected = True
        self.mod_connects += 1

    def note_mod_disconnected(self) -> None:
        if self.mod_connected:
            self.mod_disconnects += 1
        self.mod_connected = False

    def note_message(self) -> None:
        self.mod_messages += 1
        self.last_mod_message_at = time.time()

    def note_rejected(self, reason: str) -> None:
        self.rejected_messages += 1
        self.last_reject_reason = reason[:200]

    def note_unknown_type(self, message_type: str) -> None:
        self.unknown_types[message_type] += 1

    def note_hello(self, data: dict[str, Any]) -> None:
        self.mod_client = str(data.get("client", ""))[:64]
        self.mod_version = str(data.get("modVersion", ""))[:64]
        self.game_version = str(data.get("gameVersion", ""))[:64]
        capabilities = data.get("capabilities")
        self.capabilities = [str(item)[:64] for item in capabilities][:32] if isinstance(capabilities, list) else []

    @property
    def uptime(self) -> float:
        return max(0.0, time.time() - self.started_at)

    @property
    def last_mod_message_ago(self) -> float | None:
        if self.last_mod_message_at is None:
            return None
        return max(0.0, time.time() - self.last_mod_message_at)

    def snapshot(self, **extra: Any) -> dict[str, Any]:
        """doc §15 的字段 + 诊断信息。extra 由 Services 补队列 / 客户端相关计数。"""
        payload: dict[str, Any] = {
            "core": "ok",
            "protocolVersion": self.protocol_version,
            "modConnected": self.mod_connected,
            "modClient": self.mod_client,
            "modVersion": self.mod_version,
            "gameVersion": self.game_version,
            "capabilities": list(self.capabilities),
            "modMessages": self.mod_messages,
            "modConnects": self.mod_connects,
            "modDisconnects": self.mod_disconnects,
            "rejectedMessages": self.rejected_messages,
            "lastRejectReason": self.last_reject_reason,
            "lastModMessageAgoSeconds": self.last_mod_message_ago,
            "unknownMessageTypes": dict(self.unknown_types),
            "uptimeSeconds": round(self.uptime, 3),
        }
        payload.update(extra)
        return payload
