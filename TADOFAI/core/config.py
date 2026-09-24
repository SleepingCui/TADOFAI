

from __future__ import annotations

import json
import os
import re
import sys
import tempfile
from pathlib import Path
from typing import Any

from pydantic import Field, ValidationError

from .logging_setup import get_logger
from .models import WireModel

_log = get_logger("config")

#: 插件 id 同时也是配置文件名，必须限制字符集，防目录穿越
PLUGIN_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")


class ServerConfig(WireModel):
    host: str = Field(default="127.0.0.1", max_length=128)
    port: int = Field(default=37125, ge=1, le=65535)
    state_rate: int = Field(default=60, ge=1, le=240)


class PluginsConfig(WireModel):
    directory: str = Field(default="plugins", max_length=256)
    auto_reload: bool = True


class LoggingConfig(WireModel):
    level: str = Field(default="INFO", max_length=16)


class AppConfig(WireModel):
    server: ServerConfig = Field(default_factory=ServerConfig)
    plugins: PluginsConfig = Field(default_factory=PluginsConfig)
    logging: LoggingConfig = Field(default_factory=LoggingConfig)


def project_dir() -> Path:
    """TADOFAI/ 目录（core/ 的上一级）。"""
    return Path(__file__).resolve().parent.parent


def user_data_dir() -> Path:
    """打包运行时的用户数据目录。"""
    base = os.environ.get("APPDATA") or os.environ.get("XDG_DATA_HOME")
    root = Path(base) if base else Path.home()
    return root / "TADOFAI"


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def default_config_dir() -> Path:
    override = os.environ.get("TADOFAI_CONFIG_DIR")
    if override:
        return Path(override)
    # 发布版不要把用户配置写进安装目录
    return (user_data_dir() / "config") if is_frozen() else (project_dir() / "config")


def default_logs_dir() -> Path:
    override = os.environ.get("TADOFAI_LOGS_DIR")
    if override:
        return Path(override)
    return (user_data_dir() / "logs") if is_frozen() else (project_dir() / "logs")


def atomic_write_json(path: Path, payload: Any) -> None:
    """临时文件 + 替换，避免写到一半崩了留下损坏的 JSON。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp_name = tempfile.mkstemp(dir=str(path.parent), prefix=path.name, suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, path)
    except BaseException:
        try:
            os.unlink(temp_name)
        except OSError:
            pass
        raise


class ConfigManager:
    def __init__(self, config_dir: Path | None = None) -> None:
        self.config_dir = Path(config_dir) if config_dir else default_config_dir()
        self.path = self.config_dir / "config.json"
        self.plugins_dir = self.config_dir / "plugins"
        self._config = self._load()

    # --- 主配置 -----------------------------------------------------------

    def _load(self) -> AppConfig:
        if not self.path.exists():
            config = AppConfig()
            try:
                atomic_write_json(self.path, config.to_wire())
                _log.info("已生成默认配置: %s", self.path)
            except OSError as exc:
                _log.warning("默认配置写入失败（继续用内存配置）: %s", exc)
            return config

        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            _log.error("配置读取失败，改用默认配置: %s", exc)
            return AppConfig()

        try:
            return AppConfig.model_validate(raw)
        except ValidationError as exc:
            _log.error("配置校验失败，改用默认配置: %s", _brief(exc))
            return AppConfig()

    def get(self) -> AppConfig:
        return self._config

    def replace(self, config: AppConfig) -> AppConfig:
        """校验并落盘一份新配置，返回实际生效的配置。"""
        self._config = config
        self.save()
        return self._config

    def save(self) -> None:
        try:
            atomic_write_json(self.path, self._config.to_wire())
        except OSError as exc:
            _log.error("配置保存失败: %s", exc)

    # --- 插件配置 ---------------------------------------------------------

    @staticmethod
    def validate_plugin_id(plugin_id: str) -> str:
        if not isinstance(plugin_id, str) or not PLUGIN_ID_PATTERN.match(plugin_id):
            raise ValueError(f"非法插件 id: {plugin_id!r}")
        return plugin_id

    def plugin_config_path(self, plugin_id: str) -> Path:
        return self.plugins_dir / f"{self.validate_plugin_id(plugin_id)}.json"

    def get_plugin_config(self, plugin_id: str) -> dict[str, Any]:
        path = self.plugin_config_path(plugin_id)
        if not path.exists():
            return {}
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            _log.warning("插件配置读取失败 %s: %s", plugin_id, exc)
            return {}
        return data if isinstance(data, dict) else {}

    def set_plugin_config(self, plugin_id: str, data: dict[str, Any]) -> None:
        atomic_write_json(self.plugin_config_path(plugin_id), data)


def _brief(exc: ValidationError) -> str:
    """只取第一条错误，避免把整个校验树写进日志。"""
    errors = exc.errors()
    if not errors:
        return str(exc)
    first = errors[0]
    location = ".".join(str(item) for item in first.get("loc", ()))
    return f"{location}: {first.get('msg', '')}"[:200]
