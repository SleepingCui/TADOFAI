
from __future__ import annotations

import logging
import logging.handlers
import sys
from pathlib import Path

LOGGER_NAME = "tadofai"

#: 单条日志的最大长度，超出截断
MAX_LOG_MESSAGE = 2000

_LEVELS = {
    "DEBUG": logging.DEBUG,
    "INFO": logging.INFO,
    "WARNING": logging.WARNING,
    "ERROR": logging.ERROR,
    "CRITICAL": logging.CRITICAL,
}


class _TruncateFilter(logging.Filter):
    """把过长的日志截断，避免异常数据把日志刷爆。"""

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            message = record.getMessage()
        except Exception:  # 格式化失败时也不要让日志系统抛异常
            return True
        if len(message) > MAX_LOG_MESSAGE:
            record.msg = message[:MAX_LOG_MESSAGE] + " ...[已截断]"
            record.args = ()
        return True


def get_logger(name: str | None = None) -> logging.Logger:
    """取应用 logger；uvicorn 自己的 logger 不受影响。"""
    return logging.getLogger(LOGGER_NAME if not name else f"{LOGGER_NAME}.{name}")


def setup_logging(level: str = "INFO", log_dir: Path | None = None) -> logging.Logger:
    logger = get_logger()
    logger.setLevel(_LEVELS.get(str(level).upper(), logging.INFO))
    logger.propagate = False  # 只由本模块的 handler 输出，避免被 root/uvi 重复打印

    if logger.handlers:
        return logger

    formatter = logging.Formatter(
        fmt="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )
    truncate = _TruncateFilter()

    console = logging.StreamHandler(stream=sys.stdout)
    console.setFormatter(formatter)
    console.addFilter(truncate)
    logger.addHandler(console)

    if log_dir is not None:
        try:
            log_dir.mkdir(parents=True, exist_ok=True)
            file_handler = logging.handlers.RotatingFileHandler(
                log_dir / "core.log",
                maxBytes=5 * 1024 * 1024,
                backupCount=3,
                encoding="utf-8",
            )
            file_handler.setFormatter(formatter)
            file_handler.addFilter(truncate)
            logger.addHandler(file_handler)
        except OSError as exc:  # 日志目录不可写不影响服务启动
            logger.warning("日志文件初始化失败，只用控制台输出: %s", exc)

    return logger
