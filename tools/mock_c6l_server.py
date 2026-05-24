#!/usr/bin/env python3
"""
LoRa PTT — C6L WiFi 模式模擬器（UDP Mock Server）

功能：
  模擬 C6L 在基站模式下的 WiFi AP + UDP 通訊行為，
  讓 APP 在沒有實體硬體的情況下測試通訊功能。

使用方式：
  python mock_c6l_server.py [--port 5000] [--delay 500] [--mode echo|loopback|dual]

模式說明：
  echo     — 收到封包後原樣回傳（模擬對端直接回覆）
  loopback — 收到封包後模擬 LoRa 傳輸延遲再回傳
  dual     — 啟動兩個 port（5000, 5001），模擬兩台 C6L，
             A 傳的資料會轉給 B，B 傳的會轉給 A

封包格式（與韌體一致）：
  [長度高8bit][長度低8bit][payload...]
  payload = LoRa packet（SRC_ID + DST_ID + HOP + SEQ + TYPE + DATA + MAC）
"""

import argparse
import asyncio
import struct
import time
import sys
from typing import Optional

# ── 封包常數 ─────────────────────────────────────────────
HEADER_SIZE = 8       # SRC_ID(2) + DST_ID(2) + HOP(1) + SEQ(2) + TYPE(1)
MAC_SIZE = 4          # HMAC-SHA256 截斷 4 bytes
DST_BROADCAST = 0xFFFF

PKT_TYPE_TEXT = 0x01
PKT_TYPE_VOICE = 0x02
PKT_TYPE_CONTROL = 0x03
PKT_TYPE_SENSOR = 0x04

TYPE_NAMES = {
    PKT_TYPE_TEXT: "文字",
    PKT_TYPE_VOICE: "語音",
    PKT_TYPE_CONTROL: "控制",
    PKT_TYPE_SENSOR: "感測器",
}


def parse_packet(data: bytes) -> Optional[dict]:
    """解析 LoRa 封包（明文 header + payload + MAC）"""
    if len(data) < HEADER_SIZE + MAC_SIZE:
        return None

    src_id = struct.unpack(">H", data[0:2])[0]
    dst_id = struct.unpack(">H", data[2:4])[0]
    hop = data[4]
    seq = struct.unpack(">H", data[5:7])[0]
    pkt_type = data[7]
    payload = data[HEADER_SIZE:-MAC_SIZE]
    mac = data[-MAC_SIZE:]

    return {
        "src_id": src_id,
        "dst_id": dst_id,
        "hop": hop,
        "seq": seq,
        "type": pkt_type,
        "type_name": TYPE_NAMES.get(pkt_type, f"未知(0x{pkt_type:02X})"),
        "payload": payload,
        "mac": mac,
        "raw": data,
    }


def build_packet(src_id: int, dst_id: int, hop: int, seq: int,
                 pkt_type: int, payload: bytes) -> bytes:
    """組合 LoRa 封包（不含真實加密，MAC 用假值填充）"""
    header = struct.pack(">HHBHB", src_id, dst_id, hop, seq, pkt_type)
    fake_mac = b'\xDE\xAD\xBE\xEF'  # Mock 用假 MAC
    return header + payload + fake_mac


def frame_packet(data: bytes) -> bytes:
    """加上 2-byte 長度前綴（與 USB Serial 格式一致）"""
    length = len(data)
    return struct.pack(">H", length) + data


def log(msg: str):
    """帶時間戳的 log"""
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}")


# ── Echo 模式 ────────────────────────────────────────────
class EchoProtocol(asyncio.DatagramProtocol):
    """收到封包後直接回傳（可加延遲）"""

    def __init__(self, delay_ms: int = 0):
        self.delay_ms = delay_ms
        self.transport = None
        self.pkt_count = 0

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data: bytes, addr):
        self.pkt_count += 1
        pkt = parse_packet(data)

        if pkt:
            log(f"← 收到 [{pkt['type_name']}] 從 0x{pkt['src_id']:04X} "
                f"→ 0x{pkt['dst_id']:04X}, SEQ={pkt['seq']}, "
                f"payload={len(pkt['payload'])}B "
                f"(來自 {addr[0]}:{addr[1]})")
        else:
            log(f"← 收到未知格式封包 {len(data)}B 從 {addr[0]}:{addr[1]}")

        # 模擬延遲後回傳
        if self.delay_ms > 0:
            asyncio.get_event_loop().call_later(
                self.delay_ms / 1000.0,
                self._send_reply, data, addr
            )
        else:
            self._send_reply(data, addr)

    def _send_reply(self, data: bytes, addr):
        """回傳封包（模擬對端回覆）"""
        pkt = parse_packet(data)
        if pkt:
            # 交換 SRC/DST，模擬對端回覆
            reply = build_packet(
                src_id=0xB001,  # 模擬對端 ID
                dst_id=pkt["src_id"],
                hop=3,
                seq=pkt["seq"],
                pkt_type=pkt["type"],
                payload=pkt["payload"]
            )
            self.transport.sendto(reply, addr)
            log(f"→ 回傳 [{pkt['type_name']}] SEQ={pkt['seq']} "
                f"到 {addr[0]}:{addr[1]} "
                f"(延遲 {self.delay_ms}ms)")
        else:
            # 原樣回傳
            self.transport.sendto(data, addr)
            log(f"→ 原樣回傳 {len(data)}B")


# ── Dual 模式（模擬兩台 C6L）─────────────────────────────
class DualProtocol(asyncio.DatagramProtocol):
    """兩個 port 互傳：A 的資料轉給 B，B 的轉給 A"""

    def __init__(self, name: str, delay_ms: int = 0):
        self.name = name
        self.delay_ms = delay_ms
        self.transport = None
        self.peer: Optional['DualProtocol'] = None
        self.client_addr = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data: bytes, addr):
        self.client_addr = addr
        pkt = parse_packet(data)

        if pkt:
            log(f"[{self.name}] ← 收到 [{pkt['type_name']}] "
                f"SEQ={pkt['seq']}, {len(pkt['payload'])}B 從 {addr}")
        else:
            log(f"[{self.name}] ← 收到 {len(data)}B 從 {addr}")

        # 轉發給對端
        if self.peer and self.peer.client_addr:
            if self.delay_ms > 0:
                asyncio.get_event_loop().call_later(
                    self.delay_ms / 1000.0,
                    self._forward, data
                )
            else:
                self._forward(data)
        else:
            log(f"[{self.name}] ⚠ 對端尚未連線，無法轉發")

    def _forward(self, data: bytes):
        if self.peer and self.peer.client_addr:
            self.peer.transport.sendto(data, self.peer.client_addr)
            log(f"[{self.name}] → 轉發 {len(data)}B 到 "
                f"{self.peer.name} ({self.peer.client_addr})")


# ── 主程式 ────────────────────────────────────────────────
async def run_echo(port: int, delay_ms: int):
    """Echo/Loopback 模式"""
    loop = asyncio.get_event_loop()
    transport, protocol = await loop.create_datagram_endpoint(
        lambda: EchoProtocol(delay_ms),
        local_addr=("0.0.0.0", port)
    )

    log(f"=== C6L Mock Server 啟動 ===")
    log(f"模式: Echo (延遲 {delay_ms}ms)")
    log(f"監聽: UDP 0.0.0.0:{port}")
    log(f"模擬裝置 ID: 0xB001")
    log(f"按 Ctrl+C 停止")
    log(f"{'='*40}")

    try:
        await asyncio.sleep(float('inf'))
    except asyncio.CancelledError:
        pass
    finally:
        transport.close()


async def run_dual(port_a: int, port_b: int, delay_ms: int):
    """Dual 模式：兩個 port 互傳"""
    loop = asyncio.get_event_loop()

    proto_a = DualProtocol("NodeA", delay_ms)
    proto_b = DualProtocol("NodeB", delay_ms)
    proto_a.peer = proto_b
    proto_b.peer = proto_a

    transport_a, _ = await loop.create_datagram_endpoint(
        lambda: proto_a, local_addr=("0.0.0.0", port_a)
    )
    transport_b, _ = await loop.create_datagram_endpoint(
        lambda: proto_b, local_addr=("0.0.0.0", port_b)
    )

    log(f"=== C6L Mock Server 啟動（Dual 模式）===")
    log(f"NodeA 監聽: UDP 0.0.0.0:{port_a}")
    log(f"NodeB 監聽: UDP 0.0.0.0:{port_b}")
    log(f"模擬延遲: {delay_ms}ms")
    log(f"A 傳的封包會轉給 B，B 傳的會轉給 A")
    log(f"按 Ctrl+C 停止")
    log(f"{'='*40}")

    try:
        await asyncio.sleep(float('inf'))
    except asyncio.CancelledError:
        pass
    finally:
        transport_a.close()
        transport_b.close()


def main():
    parser = argparse.ArgumentParser(
        description="C6L WiFi 模式模擬器 — 用於 APP 開發測試"
    )
    parser.add_argument(
        "--port", type=int, default=5000,
        help="UDP 監聽埠號（預設 5000）"
    )
    parser.add_argument(
        "--delay", type=int, default=500,
        help="模擬 LoRa 傳輸延遲（毫秒，預設 500）"
    )
    parser.add_argument(
        "--mode", choices=["echo", "loopback", "dual"], default="echo",
        help="運行模式：echo=回傳, loopback=同echo, dual=雙節點互傳"
    )
    parser.add_argument(
        "--port-b", type=int, default=5001,
        help="Dual 模式下第二個節點的埠號（預設 5001）"
    )

    args = parser.parse_args()

    if args.mode in ("echo", "loopback"):
        asyncio.run(run_echo(args.port, args.delay))
    elif args.mode == "dual":
        asyncio.run(run_dual(args.port, args.port_b, args.delay))


if __name__ == "__main__":
    main()
