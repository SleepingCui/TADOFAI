

from __future__ import annotations

import sys
from contextlib import asynccontextmanager
from pathlib import Path
from typing import AsyncIterator

from fastapi import FastAPI
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles

from . import __version__
from .client_gateway import register_client_gateway
from .config import ConfigManager, default_logs_dir, project_dir
from .logging_setup import get_logger, setup_logging
from .mod_gateway import register_mod_gateway
from .protocol import MAX_FRAME_BYTES
from .services import Services

PROJECT_ROOT = project_dir()

# api/ 按指南 §3 放在项目根下（和 core/ 平级），不是 core 的子包
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from api import routes_config, routes_health, routes_plugins, routes_state  # noqa: E402

WEB_DIR = PROJECT_ROOT / "web"


def create_app(config_dir: Path | None = None) -> FastAPI:
    config_manager = ConfigManager(config_dir)
    config = config_manager.get()
    setup_logging(config.logging.level, default_logs_dir())
    log = get_logger("app")

    services = Services(config_manager)

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        await services.start()
        try:
            yield
        finally:
            # 关闭时必须取消所有后台任务，否则 Windows 下可能无法正常退出（§4）
            await services.stop()

    app = FastAPI(title="TADOFAI Core", version=__version__, lifespan=lifespan)
    app.state.services = services
    app.state.config_manager = config_manager

    register_mod_gateway(app, services)
    register_client_gateway(app, services)

    app.include_router(routes_health.router)
    app.include_router(routes_state.router)
    app.include_router(routes_plugins.router)
    app.include_router(routes_config.router)

    _mount_web(app, log)

    @app.get("/", include_in_schema=False)
    async def index() -> RedirectResponse:
        return RedirectResponse(url="/app/")

    log.info("管理页面: http://%s:%d/app/", config.server.host, config.server.port)
    log.info("Overlay 示例: http://%s:%d/overlay/example-overlay/", config.server.host, config.server.port)
    log.info("Mod 端点: ws://%s:%d/ws/mod", config.server.host, config.server.port)
    return app


def _mount_web(app: FastAPI, log) -> None:
    """挂载 WebUI / SDK / 公共资源。缺目录只告警，不影响服务启动（§11：没有 Mod 也能开 WebUI）。"""
    app_dir = WEB_DIR / "app"
    if app_dir.is_dir():
        app.mount("/app", StaticFiles(directory=app_dir, html=True), name="app")
    else:
        log.warning("找不到管理页面目录: %s", app_dir)

    sdk_dir = WEB_DIR / "sdk"
    if sdk_dir.is_dir():
        app.mount("/sdk", StaticFiles(directory=sdk_dir), name="sdk")
    else:
        log.warning("找不到插件 SDK 目录: %s", sdk_dir)

    assets_dir = WEB_DIR / "assets"
    if assets_dir.is_dir():
        app.mount("/assets", StaticFiles(directory=assets_dir), name="assets")


app = create_app()


def main() -> None:
    """python -m core.app：按配置启动（CLI 参数优先级更高时请用 uvicorn 命令）。"""
    import uvicorn

    config = app.state.config_manager.get()
    uvicorn.run(
        app,
        host=config.server.host,
        port=config.server.port,
        ws_max_size=MAX_FRAME_BYTES,  # 超大帧在协议层就拒掉
        log_level=str(config.logging.level).lower(),
    )


if __name__ == "__main__":
    main()
