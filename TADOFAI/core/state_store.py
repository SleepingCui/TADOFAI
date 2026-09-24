

from __future__ import annotations

import asyncio
import time

from .logging_setup import get_logger
from .models import GameState

_log = get_logger("state")


class GameStateStore:
    def __init__(self) -> None:
        self._state = GameState()
        self._lock = asyncio.Lock()
        self._updates = 0

    @property
    def update_count(self) -> int:
        return self._updates

    async def replace(self, state: GameState) -> None:
        async with self._lock:
            self._state = state
            self._updates += 1

    async def apply_mod_state(self, data: dict) -> None:
        """Mod 上报的 state 帧：整体覆盖，并标记在线 + 打上接收时刻。"""
        state = GameState.model_validate(data)
        state.connected = True
        state.stale = False
        state.updated_at = time.time()
        await self.replace(state)

    async def mark_connected(self) -> None:
        async with self._lock:
            self._state.connected = True
            self._state.stale = False

    async def mark_disconnected(self) -> None:
        """Mod 断开：保留最后状态，但标记为 stale（§17）。"""
        async with self._lock:
            self._state.connected = False
            self._state.stale = True

    async def snapshot(self) -> GameState:
        async with self._lock:
            return self._state.model_copy(deep=True)

    async def as_wire(self) -> dict:
        async with self._lock:
            return self._state.to_wire()

    async def dropped_events(self) -> int:
        async with self._lock:
            return self._state.dropped_events

    async def reset(self) -> None:
        async with self._lock:
            updated_at = self._state.updated_at
            self._state = GameState()
            self._state.updated_at = updated_at
