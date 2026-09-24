

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .config import ConfigManager, PLUGIN_ID_PATTERN, project_dir
from .logging_setup import get_logger
from .models import PLUGIN_API_VERSION

MANIFEST_NAME = "manifest.json"
DEFAULT_ENTRY = "index.html"
REQUIRED_FIELDS = ("id", "name", "version", "apiVersion", "entry")
ALLOWED_TYPES = ("overlay", "panel", "widget")

_log = get_logger("plugins")


@dataclass
class PluginInfo:
    id: str
    directory: str = ""
    name: str = ""
    version: str = ""
    api_version: int = 0
    entry: str = DEFAULT_ENTRY
    type: str = "overlay"
    permissions: list[str] = field(default_factory=list)
    default_size: dict[str, Any] = field(default_factory=dict)
    status: str = "ok"
    error: str = ""

    @property
    def valid(self) -> bool:
        return self.status == "ok"

    def to_wire(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "name": self.name,
            "version": self.version,
            "apiVersion": self.api_version,
            "entry": self.entry,
            "type": self.type,
            "permissions": list(self.permissions),
            "defaultSize": dict(self.default_size),
            "status": self.status,
            "error": self.error,
            "url": f"/overlay/{self.id}/" if self.valid else "",
        }


class PluginManager:
    def __init__(self, config_manager: ConfigManager, base_dir: Path | None = None) -> None:
        self.config_manager = config_manager
        self.base_dir = Path(base_dir) if base_dir else project_dir()
        self._plugins: list[PluginInfo] = []
        self._by_id: dict[str, PluginInfo] = {}

    # --- 目录 -------------------------------------------------------------

    def resolve_directory(self) -> Path:
        """plugins.directory 支持相对项目根或绝对路径。"""
        raw = str(self.config_manager.get().plugins.directory or "plugins").strip() or "plugins"
        directory = Path(raw)
        return directory if directory.is_absolute() else (self.base_dir / directory)

    # --- 扫描 -------------------------------------------------------------

    def scan(self) -> list[PluginInfo]:
        directory = self.resolve_directory()
        found: list[PluginInfo] = []

        if not directory.is_dir():
            _log.warning("插件目录不存在: %s", directory)
            self._plugins = []
            self._by_id = {}
            return []

        for child in sorted(directory.iterdir()):
            if not child.is_dir() or child.name.startswith((".", "_")):
                continue
            found.append(self._load_plugin(child))

        # id 冲突：保留第一个，后面的标记为错误
        by_id: dict[str, PluginInfo] = {}
        for plugin in found:
            existing = by_id.get(plugin.id)
            if existing is not None and plugin.valid:
                plugin.status = "error"
                plugin.error = f"插件 id 与 {existing.directory} 重复"

        for plugin in found:
            if plugin.id not in by_id:
                by_id[plugin.id] = plugin

        self._plugins = found
        self._by_id = by_id

        ok = sum(1 for plugin in found if plugin.valid)
        _log.info("插件扫描完成: %d 个（可用 %d）", len(found), ok)
        return list(found)

    def list(self) -> list[PluginInfo]:
        # autoReload 打开时按需重扫；重扫只更新元数据，不改动已注册的路由
        if self.config_manager.get().plugins.auto_reload:
            self.scan()
        elif not self._plugins:
            self.scan()
        return list(self._plugins)

    def get(self, plugin_id: str) -> PluginInfo | None:
        if not isinstance(plugin_id, str) or not PLUGIN_ID_PATTERN.match(plugin_id):
            return None
        if plugin_id not in self._by_id:
            self.list()
        plugin = self._by_id.get(plugin_id)
        return plugin if plugin and plugin.valid else None

    # --- 静态文件 ---------------------------------------------------------

    def resolve_file(self, plugin_id: str, relative: str | None = None) -> Path | None:
        """安全解析插件目录内的文件；越界或不存在返回 None。"""
        plugin = self.get(plugin_id)
        if plugin is None:
            return None

        relative_path = (relative or "").strip("/")
        if not relative_path:
            relative_path = plugin.entry

        base = Path(plugin.directory).resolve()
        # 去掉可能的 . / .. 片段后再拼接，最后再用 is_relative_to 复核
        candidate = (base / relative_path).resolve()
        if candidate != base and not candidate.is_relative_to(base):
            _log.warning("拒绝越界的插件文件访问: %s / %s", plugin_id, relative)
            return None
        if not candidate.is_file():
            return None
        return candidate

    # --- 单个插件 ---------------------------------------------------------

    def _load_plugin(self, directory: Path) -> PluginInfo:
        plugin = PluginInfo(id=directory.name, directory=str(directory))

        manifest_path = directory / MANIFEST_NAME
        if not manifest_path.is_file():
            plugin.status = "error"
            plugin.error = f"缺少 {MANIFEST_NAME}"
            return plugin

        try:
            raw = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            plugin.status = "error"
            plugin.error = f"{MANIFEST_NAME} 读取失败: {exc}"
            return plugin

        if not isinstance(raw, dict):
            plugin.status = "error"
            plugin.error = f"{MANIFEST_NAME} 必须是 JSON 对象"
            return plugin

        missing = [name for name in REQUIRED_FIELDS if raw.get(name) in (None, "")]
        if missing:
            plugin.status = "error"
            plugin.error = "缺少字段: " + ", ".join(missing)
            return plugin

        plugin_id = str(raw["id"])
        if not PLUGIN_ID_PATTERN.match(plugin_id):
            plugin.status = "error"
            plugin.error = f"非法 id: {plugin_id!r}（只允许字母数字和 . _ -）"
            return plugin
        plugin.id = plugin_id
        if plugin_id != directory.name:
            _log.warning("插件 id(%s) 与目录名(%s) 不一致，按 id 注册路由", plugin_id, directory.name)

        try:
            plugin.api_version = int(raw["apiVersion"])
        except (TypeError, ValueError):
            plugin.status = "error"
            plugin.error = "apiVersion 必须是整数"
            return plugin

        if plugin.api_version > PLUGIN_API_VERSION:
            plugin.status = "error"
            plugin.error = f"apiVersion {plugin.api_version} 高于本端支持的 {PLUGIN_API_VERSION}"
            return plugin

        plugin.name = str(raw.get("name", plugin_id))[:128]
        plugin.version = str(raw.get("version", ""))[:32]
        plugin.entry = str(raw.get("entry", DEFAULT_ENTRY))[:256]
        plugin.type = str(raw.get("type", "overlay"))[:32]
        plugin.permissions = [str(item)[:64] for item in raw.get("permissions", [])][:32] if isinstance(raw.get("permissions"), list) else []
        plugin.default_size = raw.get("defaultSize") if isinstance(raw.get("defaultSize"), dict) else {}

        entry_path = (directory / plugin.entry).resolve()
        if not entry_path.is_relative_to(directory.resolve()) or not entry_path.is_file():
            plugin.status = "error"
            plugin.error = f"入口文件不存在: {plugin.entry}"
            return plugin

        if plugin.type not in ALLOWED_TYPES:
            _log.warning("插件 %s 的 type=%s 不在已知类型里，按原样展示", plugin.id, plugin.type)

        return plugin
