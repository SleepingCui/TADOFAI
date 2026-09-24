

from __future__ import annotations

from fastapi import APIRouter, Request

from core.config import AppConfig
from core.logging_setup import setup_logging

router = APIRouter(tags=["config"])


@router.get("/api/config")
async def get_config(request: Request) -> dict:
    return request.app.state.config_manager.get().to_wire()


@router.post("/api/config")
async def update_config(config: AppConfig, request: Request) -> dict:
    services = request.app.state.services
    manager = request.app.state.config_manager
    previous = manager.get()

    saved = manager.replace(config)
    services.config = saved

    # --- 立即生效的部分 ---
    setup_logging(saved.logging.level)
    if (saved.plugins.directory, saved.plugins.auto_reload) != (
        previous.plugins.directory,
        previous.plugins.auto_reload,
    ):
        services.plugin_manager.scan()

    if (saved.server.host, saved.server.port) != (previous.server.host, previous.server.port):
        # 监听地址在启动时就绑定了，改它只能重启进程
        services.log.warning(
            "server.host/port 已保存（%s:%d），需要重启进程才会生效",
            saved.server.host,
            saved.server.port,
        )

    services.log.info(
        "配置已更新: stateRate=%d plugins=%s autoReload=%s level=%s",
        saved.server.state_rate,
        saved.plugins.directory,
        saved.plugins.auto_reload,
        saved.logging.level,
    )
    return saved.to_wire()
