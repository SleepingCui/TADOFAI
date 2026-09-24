

from __future__ import annotations

from typing import Annotated, Any

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, field_validator
from pydantic.alias_generators import to_camel

#: 协议版本，握手与校验都用它
PROTOCOL_VERSION = 1

#: 插件 manifest 的 apiVersion 上限
PLUGIN_API_VERSION = 1

MAX_STRING_LENGTH = 512
MAX_NAME_LENGTH = 64
MAX_PLAYER_SLOTS = 16
MAX_CAPABILITIES = 32


def _ensure_finite(value: float) -> float:
    if value != value or value in (float("inf"), float("-inf")):
        raise ValueError("数值不允许为 NaN / Infinity")
    return value


def _clamp(value: float, low: float, high: float) -> float:
    return low if value < low else (high if value > high else value)


#: 有限浮点
FiniteFloat = Annotated[float, AfterValidator(_ensure_finite)]
#: 0~1 的比例（进度、准确率），越界夹回
UnitFloat = Annotated[float, AfterValidator(_ensure_finite), AfterValidator(lambda v: _clamp(v, 0.0, 1.0))]
#: Timing 毫秒，越界夹到 ±1000
TimingMs = Annotated[float, AfterValidator(_ensure_finite), AfterValidator(lambda v: _clamp(v, -1000.0, 1000.0))]
#: BPM，负数与异常大值夹回
BpmValue = Annotated[float, AfterValidator(_ensure_finite), AfterValidator(lambda v: _clamp(v, 0.0, 1_000_000.0))]


class WireModel(BaseModel):
    """线格式基类：camelCase 别名 + 允许按字段名赋值。"""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)

    def to_wire(self) -> dict[str, Any]:
        """转换成可以直接 JSON 序列化的 camelCase dict。"""
        return self.model_dump(by_alias=True, mode="json")


class MapInfo(WireModel):
    song_name: str = Field(default="", max_length=MAX_STRING_LENGTH)
    song_author: str = Field(default="", max_length=MAX_STRING_LENGTH)
    difficulty: int = Field(default=0, ge=0, le=99)


class PlayState(WireModel):
    seq: int = Field(default=0, ge=0)
    progress: UnitFloat = 0.0
    combo: int = Field(default=0, ge=0)
    max_combo: int = Field(default=0, ge=0)
    accuracy: UnitFloat = 0.0
    x_accuracy: UnitFloat = 0.0
    bpm: BpmValue = 0.0
    timing_ms: TimingMs = 0.0
    misses: int = Field(default=0, ge=0)


class GameState(WireModel):
    """状态流：只保存最新值，由 Broadcaster 定时广播。"""

    connected: bool = False
    stale: bool = False
    game_state: str = Field(default="idle", max_length=32)
    dropped_events: int = Field(default=0, ge=0)
    auto: bool = False
    practice: bool = False
    no_fail: bool = False
    map: MapInfo = Field(default_factory=MapInfo)
    play: PlayState = Field(default_factory=PlayState)
    player_seq: list[int] = Field(default_factory=list)
    #: Core 收到该状态的时刻（epoch 秒），Mod 断开后不再变化
    updated_at: float | None = None

    @field_validator("player_seq")
    @classmethod
    def _cap_player_seq(cls, value: list[int]) -> list[int]:
        return [max(0, int(item)) for item in value[:MAX_PLAYER_SLOTS]]


class Message(WireModel):
    """协议信封：{ type, version, id, timestamp, data }。"""

    type: str = Field(min_length=1, max_length=MAX_NAME_LENGTH)
    version: int = Field(default=PROTOCOL_VERSION, ge=1, le=1000)
    timestamp: FiniteFloat = 0.0
    id: int | None = Field(default=None, ge=0)
    data: dict[str, Any] = Field(default_factory=dict)


# --- Mod -> Core 的 data 负载 ---------------------------------------------------

class HelloData(WireModel):
    client: str = Field(default="", max_length=MAX_NAME_LENGTH)
    mod_version: str = Field(default="", max_length=MAX_NAME_LENGTH)
    game_version: str = Field(default="", max_length=MAX_NAME_LENGTH)
    protocol_version: int = Field(default=0, ge=0)
    capabilities: list[str] = Field(default_factory=list)

    @field_validator("capabilities")
    @classmethod
    def _cap_capabilities(cls, value: list[str]) -> list[str]:
        return [str(item)[:MAX_NAME_LENGTH] for item in value[:MAX_CAPABILITIES]]


class HitData(WireModel):
    player: int = Field(default=0, ge=0, le=MAX_PLAYER_SLOTS)
    seq: int = Field(default=0, ge=0)
    judgement: str = Field(default="", max_length=MAX_NAME_LENGTH)
    timing_ms: TimingMs = 0.0
    combo: int = Field(default=0, ge=0)
    miss: bool = False


class EventData(WireModel):
    """game.start / game.end / map.changed / state.changed / death / checkpoint。"""

    seq: int = Field(default=0, ge=0)
    combo: int = Field(default=0, ge=0)
    detail: str = Field(default="", max_length=MAX_STRING_LENGTH)


# --- 插件客户端 -> Core ---------------------------------------------------------

class SubscribeData(WireModel):
    state: bool = True
    events: list[str] = Field(default_factory=list)

    @field_validator("events")
    @classmethod
    def _cap_events(cls, value: list[str]) -> list[str]:
        return [str(item)[:MAX_NAME_LENGTH] for item in value[:MAX_CAPABILITIES]]


# --- 消息类型表 -----------------------------------------------------------------

TYPE_HELLO = "hello"
TYPE_STATE = "state"
TYPE_HIT = "hit"
TYPE_SUBSCRIBE = "subscribe"

EVENT_TYPES: tuple[str, ...] = (
    "game.start",
    "game.end",
    "map.changed",
    "state.changed",
    "death",
    "checkpoint",
)

#: 已知类型 -> 负载模型；不在表里的类型会记一条警告并原样转发给插件
PAYLOAD_MODELS: dict[str, type[WireModel]] = {
    TYPE_HELLO: HelloData,
    TYPE_STATE: GameState,
    TYPE_HIT: HitData,
    TYPE_SUBSCRIBE: SubscribeData,
    **{name: EventData for name in EVENT_TYPES},
}
