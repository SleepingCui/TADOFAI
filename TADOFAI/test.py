#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TADOFAI 测试服务端

监听 ws://127.0.0.1:37125/ws/mod（就是 TADOFAI.Mod 的默认 Settings.ServerUrl），
把 Mod 发来的消息打印出来，用来验证协议和字段。只做测试用，不实现 Core 的任何逻辑。

用法:
    python test.py                # 原地刷新显示 state，事件逐条打印
    python test.py --all          # 每一条 state 都单独打印（60Hz 会刷屏，排查时用）
    python test.py --raw          # 额外打印原始 JSON
    python test.py --port 37126   # 换端口（记得同步改 Mod 的 Settings.xml）

依赖: websockets（pip install websockets）
退出: Ctrl+C 打印统计
"""

import argparse
import asyncio
import json
import sys
import time
from collections import Counter

try:
    import websockets
except ImportError:
    sys.exit("缺少依赖，请先安装: pip install websockets")

# 控制台编码不匹配时不要让打印抛异常
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(errors="replace")
    except Exception:
        pass


def num(value, default=0.0):
    """把 JSON 里的值安全转成 float，缺失或非法时给默认值。"""
    try:
        result = float(value)
    except (TypeError, ValueError):
        return default
    if result != result or result in (float("inf"), float("-inf")):  # NaN / Infinity
        return default
    return result


class Printer:
    """
    state 是 30~120Hz 的状态流，逐条打印没法看，所以默认用 \\r 原地刷新成一行状态；
    事件（hit / game.start ...）正常另起一行打印，打印前先把状态行清掉。
    """

    def __init__(self, show_all_state=False, raw=False, state_hz=5.0):
        self.show_all_state = show_all_state
        self.raw = raw
        self.min_interval = 1.0 / max(0.1, state_hz)
        self._last_state_at = 0.0
        self._status_len = 0

    def info(self, text):
        self._clear_status()
        print(text, flush=True)

    def event(self, text):
        self._clear_status()
        print(text, flush=True)

    def state(self, text):
        if self.show_all_state:
            self._clear_status()
            print(text, flush=True)
            return

        now = time.monotonic()
        if now - self._last_state_at < self.min_interval:
            return
        self._last_state_at = now

        line = text[:200]
        sys.stdout.write("\r" + line)
        if len(line) < self._status_len:
            sys.stdout.write(" " * (self._status_len - len(line)))
        sys.stdout.flush()
        self._status_len = len(line)

    def finish(self):
        self._clear_status()

    def _clear_status(self):
        if self._status_len:
            sys.stdout.write("\r" + " " * self._status_len + "\r")
            self._status_len = 0


class Stats:
    def __init__(self):
        self.counts = Counter()
        self.conn_total = 0
        self.conn_now = 0
        self.messages = 0
        self.bytes_in = 0
        self.last_id = None
        self.id_gap = 0
        self.id_bad = 0

    def count(self, mtype):
        self.counts[mtype] += 1

    def check_id(self, message_id):
        """协议里 id 用来检测丢失和重复，只对逐条事件有意义（state 没有 id）。"""
        if not isinstance(message_id, int):
            return
        if self.last_id is not None:
            if message_id <= self.last_id:
                self.id_bad += 1
            elif message_id > self.last_id + 1:
                self.id_gap += message_id - self.last_id - 1
        self.last_id = message_id

    def report(self):
        print("\n================ 统计 ================")
        print(f"连接次数 {self.conn_total}    消息总数 {self.messages}    接收字节 {self.bytes_in}")
        if self.counts:
            print("按类型:")
            for mtype, count in self.counts.most_common():
                print(f"  {mtype:<14} {count}")
        print(f"事件 id 缺口 {self.id_gap}    重复/乱序 {self.id_bad}")


def format_state(data):
    play = data.get("play") or {}
    game_map = data.get("map") or {}
    line = (
        "[state] {game} seq={seq} prog={prog:.1f}% combo={combo} acc={acc:.2f}% xacc={xacc:.2f}% "
        "bpm={bpm:.1f} timing={timing:+.2f}ms miss={miss} auto={auto} dropped={dropped}"
    ).format(
        game=data.get("gameState", "?"),
        seq=int(num(play.get("seq"))),
        prog=num(play.get("progress")) * 100.0,
        combo=int(num(play.get("combo"))),
        acc=num(play.get("accuracy")) * 100.0,
        xacc=num(play.get("xAccuracy")) * 100.0,
        bpm=num(play.get("bpm")),
        timing=num(play.get("timingMs")),
        miss=int(num(play.get("misses"))),
        auto="Y" if data.get("auto") else "N",
        dropped=int(num(data.get("droppedEvents"))),
    )
    song = game_map.get("songName")
    if song:
        line += f"  song={song}"
    return line


def format_hit(data):
    line = "[hit]   player={player} seq={seq} {judgement} timing={timing:+.2f}ms combo={combo}".format(
        player=int(num(data.get("player"))),
        seq=int(num(data.get("seq"))),
        judgement=data.get("judgement", "?"),
        timing=num(data.get("timingMs")),
        combo=int(num(data.get("combo"))),
    )
    if data.get("miss"):
        line += "  (断 Combo)"
    return line


def handle_message(raw, printer, stats, args):
    stats.messages += 1
    stats.bytes_in += len(raw)

    try:
        message = json.loads(raw)
    except Exception as exc:
        printer.event(f"[协议错误] 不是合法 JSON: {exc}    原文: {raw[:200]!r}")
        return

    if not isinstance(message, dict):
        printer.event(f"[协议错误] 顶层不是 JSON 对象: {raw[:200]!r}")
        return

    mtype = message.get("type")
    if not isinstance(mtype, str) or not mtype:
        printer.event(f"[协议错误] 缺少 type 字段: {raw[:200]!r}")
        return

    stats.count(mtype)
    stats.check_id(message.get("id"))
    data = message.get("data") or {}
    if not isinstance(data, dict):
        data = {}

    if mtype == "state":
        printer.state(format_state(data))
    elif mtype == "hit":
        printer.event(format_hit(data))
    elif mtype == "hello":
        capabilities = data.get("capabilities") or []
        printer.event(
            "[hello] client={client} mod={mod} game={game} protocol={proto} capabilities={caps}".format(
                client=data.get("client", "?"),
                mod=data.get("modVersion", "?"),
                game=data.get("gameVersion", "?"),
                proto=message.get("version", "?"),
                caps=",".join(str(c) for c in capabilities),
            )
        )
    else:
        # game.start / game.end / map.changed / state.changed / death / checkpoint
        detail = "  ".join(f"{k}={v}" for k, v in data.items())
        printer.event(f"[{mtype}] {detail}")

    if args.raw:
        printer.event(f"        raw: {raw[:400]!r}")


async def connection_handler(websocket, args, printer, stats):
    request = getattr(websocket, "request", None)
    path = getattr(request, "path", "?") or "?"
    peer = str(websocket.remote_address)

    stats.conn_total += 1
    stats.conn_now += 1
    printer.info(f"[连接] {peer}  path={path}  当前连接数={stats.conn_now}")
    if args.path and path != args.path:
        printer.info(f"[提示] 路径不是 {args.path}，检查 Mod 的 Settings.ServerUrl 是否一致")

    try:
        async for raw in websocket:
            handle_message(raw, printer, stats, args)
    except websockets.ConnectionClosed as exc:
        printer.info(f"[断开] {peer}  code={exc.code} reason={exc.reason!r}")
    except Exception as exc:  # 测试服务端不能因为一条坏消息就退出
        printer.info(f"[异常] {peer}: {type(exc).__name__}: {exc}")
    finally:
        stats.conn_now -= 1
        printer.finish()


async def serve(args, printer, stats):
    printer.info(f"TADOFAI 测试服务端  监听 ws://{args.host}:{args.port}{args.path}")
    printer.info("等待 Mod 连接（Ctrl+C 退出）...")

    async with websockets.serve(
        lambda websocket: connection_handler(websocket, args, printer, stats),
        args.host,
        args.port,
        # 关掉服务端主动 ping：即使是没实现 pong 的客户端也让它挂着，方便看数据
        ping_interval=None,
    ):
        await asyncio.Future()


def main():
    parser = argparse.ArgumentParser(description="TADOFAI 测试服务端：接收并打印 Mod 发来的消息")
    parser.add_argument("--host", default="127.0.0.1", help="监听地址，默认 127.0.0.1")
    parser.add_argument("--port", type=int, default=37125, help="监听端口，默认 37125")
    parser.add_argument("--path", default="/ws/mod", help="期望的 WebSocket 路径，默认 /ws/mod")
    parser.add_argument("--all", action="store_true", help="每条 state 都单独打印（默认原地刷新）")
    parser.add_argument("--raw", action="store_true", help="额外打印原始 JSON")
    args = parser.parse_args()

    printer = Printer(show_all_state=args.all, raw=args.raw)
    stats = Stats()

    try:
        asyncio.run(serve(args, printer, stats))
    except KeyboardInterrupt:
        printer.finish()
    stats.report()


if __name__ == "__main__":
    main()
