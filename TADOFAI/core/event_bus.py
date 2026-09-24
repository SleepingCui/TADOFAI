

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from typing import Any, Iterable

#: 单个插件客户端的事件队列上限
DEFAULT_EVENT_QUEUE_SIZE = 4096

#: 订阅 events 时可以用 "*" 表示全部事件
WILDCARD = "*"


@dataclass
class Subscriber:
    id: int
    want_state: bool = True
    events: set[str] = field(default_factory=set)
    queue: asyncio.Queue = field(default_factory=lambda: asyncio.Queue(DEFAULT_EVENT_QUEUE_SIZE))
    connected_at: float = field(default_factory=time.time)

    dropped: int = 0
    sent: int = 0
    state_slot: dict[str, Any] | None = None
    state_updates: int = 0

    def wants(self, message_type: str) -> bool:
        return bool(self.events) and (message_type in self.events or WILDCARD in self.events)

    def take_state(self) -> dict[str, Any] | None:
        message, self.state_slot = self.state_slot, None
        return message

    def take_event(self) -> dict[str, Any] | None:
        try:
            return self.queue.get_nowait()
        except asyncio.QueueEmpty:
            return None

    def stats(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "wantState": self.want_state,
            "events": sorted(self.events),
            "pending": self.queue.qsize(),
            "dropped": self.dropped,
            "sent": self.sent,
            "connectedAt": self.connected_at,
        }


class EventBus:
    def __init__(self, queue_size: int = DEFAULT_EVENT_QUEUE_SIZE) -> None:
        self.queue_size = max(16, int(queue_size))
        self._subscribers: dict[int, Subscriber] = {}
        self._next_id = 1

    # --- 订阅管理 ---------------------------------------------------------

    def subscribe(self, *, want_state: bool = True, events: Iterable[str] = ()) -> Subscriber:
        subscriber = Subscriber(
            id=self._next_id,
            want_state=want_state,
            events=set(events or ()),
            queue=asyncio.Queue(self.queue_size),
        )
        self._next_id += 1
        self._subscribers[subscriber.id] = subscriber
        return subscriber

    def unsubscribe(self, subscriber: Subscriber) -> None:
        self._subscribers.pop(subscriber.id, None)

    def update_subscription(self, subscriber: Subscriber, *, want_state: bool, events: Iterable[str]) -> None:
        subscriber.want_state = want_state
        subscriber.events = set(events or ())
        if not want_state:
            subscriber.state_slot = None

    @property
    def client_count(self) -> int:
        return len(self._subscribers)

    # --- 发布（同步，绝不等待客户端）---------------------------------------

    def publish_event(self, message: dict[str, Any]) -> int:
        """把事件投给订阅了它的客户端；满时丢最旧。返回投递的客户端数。"""
        message_type = message.get("type", "")
        delivered = 0
        for subscriber in list(self._subscribers.values()):
            if not subscriber.wants(message_type):
                continue
            delivered += 1
            while subscriber.queue.full():
                try:
                    subscriber.queue.get_nowait()
                    subscriber.dropped += 1
                except asyncio.QueueEmpty:
                    break
            try:
                subscriber.queue.put_nowait(message)
            except asyncio.QueueFull:  # 理论上到不了，兜底
                subscriber.dropped += 1
        return delivered

    def publish_state(self, message: dict[str, Any]) -> int:
        """状态覆盖旧值：只保留最新一份。"""
        delivered = 0
        for subscriber in list(self._subscribers.values()):
            if not subscriber.want_state:
                continue
            subscriber.state_slot = message
            subscriber.state_updates += 1
            delivered += 1
        return delivered

    # --- 统计 -------------------------------------------------------------

    @property
    def total_pending_events(self) -> int:
        return sum(subscriber.queue.qsize() for subscriber in self._subscribers.values())

    @property
    def total_dropped_events(self) -> int:
        return sum(subscriber.dropped for subscriber in self._subscribers.values())

    @property
    def max_pending_events(self) -> int:
        if not self._subscribers:
            return 0
        return max(subscriber.queue.qsize() for subscriber in self._subscribers.values())

    def subscriber_stats(self) -> list[dict[str, Any]]:
        return [subscriber.stats() for subscriber in self._subscribers.values()]
