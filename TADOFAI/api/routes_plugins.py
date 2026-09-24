

from __future__ import annotations

import mimetypes
from pathlib import Path

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import FileResponse, RedirectResponse

from core.models import PLUGIN_API_VERSION

router = APIRouter(tags=["plugins"])

#: 这些类型带上 charset，中文内容不会乱码
_CHARSET_TYPES = frozenset(
    {
        "text/html",
        "text/css",
        "text/javascript",
        "application/javascript",
        "application/json",
        "image/svg+xml",
    }
)


def _media_type(path: Path) -> str:
    guessed = mimetypes.guess_type(str(path))[0] or "application/octet-stream"
    return f"{guessed}; charset=utf-8" if guessed in _CHARSET_TYPES else guessed


@router.get("/api/plugins")
async def list_plugins(request: Request) -> dict:
    services = request.app.state.services
    plugins = services.plugin_manager.list()
    return {
        "apiVersion": PLUGIN_API_VERSION,
        "directory": str(services.plugin_manager.resolve_directory()),
        "count": len(plugins),
        "usable": sum(1 for plugin in plugins if plugin.valid),
        "plugins": [plugin.to_wire() for plugin in plugins],
    }


@router.get("/api/plugin/{plugin_id}/manifest")
async def get_manifest(plugin_id: str, request: Request) -> dict:
    plugin = request.app.state.services.plugin_manager.get(plugin_id)
    if plugin is None:
        raise HTTPException(status_code=404, detail=f"插件不存在或不可用: {plugin_id}")
    return plugin.to_wire()


@router.get("/overlay/{plugin_id}")
async def overlay_redirect(plugin_id: str) -> RedirectResponse:
    """补个结尾斜杠，否则页面里的相对路径会算到上一级去。"""
    return RedirectResponse(url=f"/overlay/{plugin_id}/")


@router.get("/overlay/{plugin_id}/{path:path}")
async def overlay_file(plugin_id: str, path: str, request: Request) -> FileResponse:
    return _serve_plugin_file(request, plugin_id, path)


@router.get("/plugins/{plugin_id}/{path:path}")
async def plugins_file(plugin_id: str, path: str, request: Request) -> FileResponse:
    """§12 允许统一从 /plugins/{id}/ 提供，这里作为别名。"""
    return _serve_plugin_file(request, plugin_id, path)


def _serve_plugin_file(request: Request, plugin_id: str, path: str) -> FileResponse:
    target = request.app.state.services.plugin_manager.resolve_file(plugin_id, path)
    if target is None:
        raise HTTPException(status_code=404, detail=f"文件不存在: {plugin_id}/{path}")
    return FileResponse(target, media_type=_media_type(target))
