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
import json
import zlib
from typing import Optional

# ── 封包常數 ─────────────────────────────────────────────
HEADER_SIZE = 8       # SRC_ID(2) + DST_ID(2) + HOP(1) + SEQ(2) + TYPE(1)
MAC_SIZE = 4          # MAC 長度（Phase1 CRC32 / Phase2 HMAC 截斷）
HOP_OFFSET = 4        # HOP 欄位位移（MAC 計算時跳過，與韌體一致）
DST_BROADCAST = 0xFFFF
DST_GROUP_MIN = 0xFFE0
DST_GROUP_MAX = 0xFFEF

# ── 線路幀類型（手機 ↔ C6L，與韌體 main.cpp 一致）────────────
LINK_DATA = 0x01      # [01][LoRa封包]（phone→C6L）；[01][RSSI int16 BE][LoRa封包]（C6L→phone）
LINK_CTRL = 0x02      # [02][JSON]
MOCK_DEVICE_ID = 0xB001
MOCK_FW_VER = "mock-1.0"
MOCK_RSSI = -75       # 模擬回傳的 RSSI（dBm）


def wrap_data(packet: bytes, rssi: int = MOCK_RSSI) -> bytes:
    """C6L→phone DATA 幀：[01][RSSI int16 BE][packet]"""
    return bytes([LINK_DATA]) + struct.pack(">h", rssi) + packet


def wrap_ctrl(obj: dict) -> bytes:
    """CTRL 幀：[02][JSON]"""
    return bytes([LINK_CTRL]) + json.dumps(obj).encode("utf-8")


def make_hello_ack() -> bytes:
    return wrap_ctrl({
        "status": "hello",
        "device_id": MOCK_DEVICE_ID,
        "name": "MockC6L",
        "fw_ver": MOCK_FW_VER,
    })

PKT_TYPE_TEXT = 0x01
PKT_TYPE_VOICE = 0x02
PKT_TYPE_CONTROL = 0x03
PKT_TYPE_ACK = 0x04
PKT_TYPE_PING = 0x05

TYPE_NAMES = {
    PKT_TYPE_TEXT: "文字",
    PKT_TYPE_VOICE: "語音",
    PKT_TYPE_CONTROL: "控制",
    PKT_TYPE_ACK: "ACK",
    PKT_TYPE_PING: "PING",
}


def compute_mac(header: bytes, payload: bytes) -> bytes:
    """計算 Phase1 CRC32 MAC（跳過 HOP 欄位，與韌體 packetMac 一致）"""
    data = header[:HOP_OFFSET] + header[HOP_OFFSET + 1:] + payload
    return struct.pack(">I", zlib.crc32(data) & 0xFFFFFFFF)


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
    """組合 LoRa 封包（Phase1 不加密，MAC 用真實 CRC32，與韌體一致）"""
    header = struct.pack(">HHBHB", src_id, dst_id, hop, seq, pkt_type)
    mac = compute_mac(header, payload)
    return header + payload + mac


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
        if len(data) < 1:
            return
        link = data[0]

        # CTRL 幀：握手 / 設定
        if link == LINK_CTRL:
            self._handle_ctrl(data[1:], addr)
            return

        # DATA 幀：phone→C6L = [01][packet]（無 RSSI）
        if link != LINK_DATA:
            log(f"← 收到未知線路幀類型 0x{link:02X}（{len(data)}B）")
            return

        pkt = parse_packet(data[1:])
        if pkt:
            log(f"← DATA [{pkt['type_name']}] 從 0x{pkt['src_id']:04X} "
                f"→ 0x{pkt['dst_id']:04X}, SEQ={pkt['seq']}, "
                f"payload={len(pkt['payload'])}B")
        else:
            log(f"← DATA 但封包格式錯誤（{len(data)-1}B）")
            return

        # 模擬延遲後回傳
        if self.delay_ms > 0:
            asyncio.get_event_loop().call_later(
                self.delay_ms / 1000.0, self._send_reply, pkt, addr)
        else:
            self._send_reply(pkt, addr)

    def _handle_ctrl(self, body: bytes, addr):
        try:
            obj = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            log(f"← CTRL 但 JSON 解析失敗：{body!r}")
            return
        cmd = obj.get("cmd")
        if cmd == "hello":
            log(f"← CTRL hello（APP: {obj.get('name')} v{obj.get('app_ver')}）→ 回 hello-ack")
            self.transport.sendto(make_hello_ack(), addr)
        elif cmd == "set_config":
            log(f"← CTRL set_config {obj} → 回 ok")
            self.transport.sendto(wrap_ctrl({"status": "ok"}), addr)
        else:
            log(f"← CTRL 未知 cmd：{cmd}")

    def _send_reply(self, pkt: dict, addr):
        """回傳封包（模擬對端回覆，交換 SRC/DST）"""
        reply = build_packet(
            src_id=MOCK_DEVICE_ID,    # 模擬對端 ID
            dst_id=pkt["src_id"],
            hop=3,
            seq=pkt["seq"],
            pkt_type=pkt["type"],
            payload=pkt["payload"],
        )
        self.transport.sendto(wrap_data(reply), addr)
        log(f"→ DATA [{pkt['type_name']}] SEQ={pkt['seq']} 回 {addr[0]}:{addr[1]} "
            f"(RSSI={MOCK_RSSI}, 延遲 {self.delay_ms}ms)")


# ── Dual 模式（模擬兩台 C6L）─────────────────────────────
class DualProtocol(asyncio.DatagramProtocol):
    """兩個 port 互傳：A 的資料轉給 B，B 的轉給 A"""

    def __init__(self, name: str, node_id: int, delay_ms: int = 0):
        self.name = name
        self.node_id = node_id
        self.delay_ms = delay_ms
        self.transport = None
        self.peer: Optional['DualProtocol'] = None
        self.client_addr = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data: bytes, addr):
        self.client_addr = addr
        if len(data) < 1:
            return
        link = data[0]

        # CTRL 握手 / 設定（由本節點回覆）
        if link == LINK_CTRL:
            try:
                obj = json.loads(data[1:].decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                return
            if obj.get("cmd") == "hello":
                ack = wrap_ctrl({"status": "hello", "device_id": self.node_id,
                                 "name": self.name, "fw_ver": MOCK_FW_VER})
                self.transport.sendto(ack, addr)
                log(f"[{self.name}] ← hello → 回 ack (id=0x{self.node_id:04X})")
            elif obj.get("cmd") == "set_config":
                self.transport.sendto(wrap_ctrl({"status": "ok"}), addr)
            return

        if link != LINK_DATA:
            return

        pkt = parse_packet(data[1:])
        if not pkt:
            log(f"[{self.name}] ← DATA 封包格式錯誤")
            return
        log(f"[{self.name}] ← DATA [{pkt['type_name']}] SEQ={pkt['seq']} "
            f"→ 0x{pkt['dst_id']:04X}")

        # 本節點「發射」：SRC 改為本節點 ID（模擬韌體 stamp），再轉給對端手機
        if self.peer and self.peer.client_addr:
            relayed = build_packet(self.node_id, pkt["dst_id"], 3,
                                   pkt["seq"], pkt["type"], pkt["payload"])
            if self.delay_ms > 0:
                asyncio.get_event_loop().call_later(
                    self.delay_ms / 1000.0, self._forward, relayed)
            else:
                self._forward(relayed)
        else:
            log(f"[{self.name}] ⚠ 對端手機尚未連線，無法轉發")

    def _forward(self, packet: bytes):
        if self.peer and self.peer.client_addr:
            self.peer.transport.sendto(wrap_data(packet), self.peer.client_addr)
            log(f"[{self.name}] → 轉發到 {self.peer.name} ({self.peer.client_addr})")


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

    proto_a = DualProtocol("NodeA", 0xA001, delay_ms)
    proto_b = DualProtocol("NodeB", 0xB001, delay_ms)
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
