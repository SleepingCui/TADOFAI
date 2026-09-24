

from __future__ import annotations

import json
import time
from dataclasses import dataclass
from typing import Any

from pydantic import ValidationError

from .models import (
    EVENT_TYPES,
    MAX_STRING_LENGTH,
    Message,
    PAYLOAD_MODELS,
    PROTOCOL_VERSION,
    TYPE_STATE,
    TYPE_SUBSCRIBE,
    WireModel,
)

#: 单帧上限，超过直接拒绝
MAX_FRAME_BYTES = 256 * 1024
#: data 的递归深度与容器元素数上限
MAX_DEPTH = 8
MAX_ITEMS = 4096

#: 当前支持的协议版本
SUPPORTED_VERSIONS = frozenset({PROTOCOL_VERSION})

try:  # pragma: no cover - 取决于环境
    import orjson

    def _json_loads(text: str) -> Any:
        return orjson.loads(text)

    def _json_dumps(payload: Any) -> str:
        return orjson.dumps(payload).decode("utf-8")

    SERIALIZER = "orjson"
except ImportError:  # pragma: no cover
    def _json_loads(text: str) -> Any:
        return json.loads(text)

    def _json_dumps(payload: Any) -> str:
        # allow_nan=False：sanitize 之后不该再有非有限值，真出现就报错而不是产出非法 JSON
        return json.dumps(payload, ensure_ascii=False, allow_nan=False, separators=(",", ":"))

    SERIALIZER = "json"


class ProtocolError(Exception):
    """协议层异常基类。"""


class UnsupportedVersion(ProtocolError):
    """协议版本不支持。"""


class InvalidMessage(ProtocolError):
    """消息格式或取值非法。"""


@dataclass(slots=True)
class ParsedMessage:
    """解析结果。data 已转成 camelCase，可以直接转发给插件。"""

    message: Message
    data: dict[str, Any]
    model: WireModel | None
    known: bool

    @property
    def type(self) -> str:
        return self.message.type

    @property
    def id(self) -> int | None:
        return self.message.id

    @property
    def timestamp(self) -> float:
        return self.message.timestamp


def now() -> float:
    """出站消息统一用 epoch 秒，前端可以直接和 Date.now() 对齐。"""
    return time.time()


def sanitize(value: Any, depth: int = 0) -> Any:
    """把非有限浮点换成 None，保证序列化结果里没有 NaN / Infinity。"""
    if depth > MAX_DEPTH:
        return None
    if isinstance(value, float):
        return value if value == value and value not in (float("inf"), float("-inf")) else None
    if isinstance(value, dict):
        return {
            str(key)[:MAX_STRING_LENGTH]: sanitize(item, depth + 1)
            for key, item in list(value.items())[:MAX_ITEMS]
        }
    if isinstance(value, (list, tuple, set)):
        return [sanitize(item, depth + 1) for item in list(value)[:MAX_ITEMS]]
    if isinstance(value, (str, int, bool)) or value is None:
        return value
    return str(value)[:MAX_STRING_LENGTH]


def dumps(payload: Any) -> str:
    return _json_dumps(sanitize(payload))


def validate_raw(value: Any, depth: int = 0) -> None:
    """对任意 JSON 结构做值检查（未知消息类型也走这里）。"""
    if depth > MAX_DEPTH:
        raise InvalidMessage("嵌套层数过深")
    if isinstance(value, float):
        if value != value or value in (float("inf"), float("-inf")):
            raise InvalidMessage("数值不允许为 NaN / Infinity")
        return
    if isinstance(value, str):
        if len(value) > MAX_STRING_LENGTH:
            raise InvalidMessage(f"字符串超长（{len(value)} > {MAX_STRING_LENGTH}）")
        return
    if isinstance(value, dict):
        if len(value) > MAX_ITEMS:
            raise InvalidMessage("对象字段过多")
        for key, item in value.items():
            if not isinstance(key, str):
                raise InvalidMessage("对象键必须是字符串")
            validate_raw(item, depth + 1)
        return
    if isinstance(value, (list, tuple)):
        if len(value) > MAX_ITEMS:
            raise InvalidMessage("数组元素过多")
        for item in value:
            validate_raw(item, depth + 1)


def parse_message(raw: str | bytes, *, expect: str | None = None) -> ParsedMessage:
    """解析并校验一条入站消息（Mod 与插件客户端共用）。

    expect 不为 None 时要求消息类型匹配，用于 /ws/client 的 subscribe。
    """
    text = _decode_text(raw)

    try:
        payload = _json_loads(text)
    except Exception as exc:
        raise InvalidMessage(f"不是合法 JSON: {exc}") from exc

    if not isinstance(payload, dict):
        raise InvalidMessage("顶层必须是 JSON 对象")

    try:
        message = Message.model_validate(payload)
    except ValidationError as exc:
        raise InvalidMessage(f"信封校验失败: {_brief(exc)}") from exc

    if message.version not in SUPPORTED_VERSIONS:
        raise UnsupportedVersion(
            f"不支持的协议版本 {message.version}，本端支持 {sorted(SUPPORTED_VERSIONS)}"
        )

    if expect is not None and message.type != expect:
        raise InvalidMessage(f"期望 {expect}，收到 {message.type}")

    if expect is None and message.type == TYPE_SUBSCRIBE:
        raise InvalidMessage("subscribe 只用于 /ws/client")

    # 未知类型也要挡住 NaN / 超长字符串
    validate_raw(message.data)

    model_type = PAYLOAD_MODELS.get(message.type)
    if model_type is None:
        # 未知类型：由调用方记警告，data 原样转发
        return ParsedMessage(message=message, data=message.data, model=None, known=False)

    try:
        model = model_type.model_validate(message.data)
    except ValidationError as exc:
        raise InvalidMessage(f"{message.type} 负载校验失败: {_brief(exc)}") from exc

    return ParsedMessage(message=message, data=model.to_wire(), model=model, known=True)


def parse_subscribe(raw: str | bytes) -> ParsedMessage:
    """解析插件客户端的订阅消息。"""
    return parse_message(raw, expect=TYPE_SUBSCRIBE)


def make_message(
    message_type: str,
    data: dict[str, Any] | WireModel | None = None,
    *,
    message_id: int | None = None,
    timestamp: float | None = None,
) -> dict[str, Any]:
    """构造出站信封。"""
    if isinstance(data, WireModel):
        payload: dict[str, Any] = data.to_wire()
    else:
        payload = data or {}

    message: dict[str, Any] = {
        "type": message_type,
        "version": PROTOCOL_VERSION,
        "timestamp": now() if timestamp is None else timestamp,
        "data": payload,
    }
    if message_id is not None:
        message["id"] = message_id
    return message


def is_event_type(message_type: str) -> bool:
    """状态帧之外的消息都按事件转发（未知类型也一样，便于插件自己扩展）。"""
    return message_type not in (TYPE_SUBSCRIBE, TYPE_STATE)


def known_event_types() -> tuple[str, ...]:
    return EVENT_TYPES


def _decode_text(raw: str | bytes) -> str:
    if isinstance(raw, (bytes, bytearray)):
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise InvalidMessage("不是合法的 UTF-8") from exc
    else:
        text = raw

    if len(text) > MAX_FRAME_BYTES:
        raise InvalidMessage(f"帧过大（{len(text)} > {MAX_FRAME_BYTES}）")
    return text


def _brief(exc: ValidationError) -> str:
    errors = exc.errors()
    if not errors:
        return str(exc)
    first = errors[0]
    location = ".".join(str(item) for item in first.get("loc", ()))
    return f"{location}: {first.get('msg', '')}"[:200]
