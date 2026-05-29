#!/usr/bin/env python3
"""
LoRa PTT — Mock Server 測試客戶端

用途：模擬手機 APP 發送封包到 C6L Mock Server，驗證收發正常。

使用方式：
  python test_client.py [--host 127.0.0.1] [--port 5000] [--mode text|voice]
"""

import argparse
import asyncio
import struct
import time
import sys
import json
import zlib

HOP_OFFSET = 4  # MAC 計算時跳過的 HOP 位移（與韌體一致）

# ── 線路幀（與韌體 main.cpp / mock server 一致）──────────────
LINK_DATA = 0x01
LINK_CTRL = 0x02


def build_packet(src_id: int, dst_id: int, hop: int, seq: int,
                 pkt_type: int, payload: bytes) -> bytes:
    """組合 LoRa 封包（Phase1 不加密，MAC 用真實 CRC32，與韌體一致）"""
    header = struct.pack(">HHBHB", src_id, dst_id, hop, seq, pkt_type)
    data = header[:HOP_OFFSET] + header[HOP_OFFSET + 1:] + payload
    mac = struct.pack(">I", zlib.crc32(data) & 0xFFFFFFFF)
    return header + payload + mac


def wrap_data(packet: bytes) -> bytes:
    """phone→C6L DATA 幀：[01][packet]"""
    return bytes([LINK_DATA]) + packet


def wrap_hello(name: str = "TestClient") -> bytes:
    """CTRL hello 握手幀"""
    return bytes([LINK_CTRL]) + json.dumps(
        {"cmd": "hello", "name": name, "app_ver": "test"}).encode("utf-8")


def parse_packet(data: bytes) -> dict:
    """解析收到的封包"""
    if len(data) < 12:
        return {"raw": data, "error": "太短"}
    src_id = struct.unpack(">H", data[0:2])[0]
    dst_id = struct.unpack(">H", data[2:4])[0]
    hop = data[4]
    seq = struct.unpack(">H", data[5:7])[0]
    pkt_type = data[7]
    payload = data[8:-4]
    mac = data[-4:]
    return {
        "src_id": src_id, "dst_id": dst_id, "hop": hop,
        "seq": seq, "type": pkt_type, "payload": payload, "mac": mac
    }


class TestClient(asyncio.DatagramProtocol):
    def __init__(self):
        self.transport = None
        self.received = asyncio.Event()
        self.last_data = None

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        self.last_data = data
        if len(data) < 1:
            return
        link = data[0]
        if link == LINK_CTRL:
            print(f"  ← CTRL: {data[1:].decode('utf-8', 'replace')}")
            self.received.set()
            return
        if link != LINK_DATA:
            print(f"  ← 未知線路幀 0x{link:02X}")
            return
        # C6L→phone DATA：[01][RSSI int16 BE][packet]
        rssi = struct.unpack(">h", data[1:3])[0]
        pkt = parse_packet(data[3:])
        print(f"  ← DATA 回覆: SRC=0x{pkt['src_id']:04X} "
              f"DST=0x{pkt['dst_id']:04X} SEQ={pkt['seq']} "
              f"payload={len(pkt['payload'])}B RSSI={rssi}dBm")
        self.received.set()


async def run_text_test(host: str, port: int):
    """發送文字封包測試"""
    loop = asyncio.get_event_loop()
    transport, protocol = await loop.create_datagram_endpoint(
        TestClient, remote_addr=(host, port)
    )

    print(f"=== 文字訊息測試 ===")
    print(f"目標: {host}:{port}")
    print()

    # 先握手（F-053）
    transport.sendto(wrap_hello())
    print("  → 送出握手 hello")
    await asyncio.sleep(0.3)

    messages = ["Hello LoRa!", "測試中文", "PTT 對講機"]

    for i, msg in enumerate(messages):
        payload = msg.encode("utf-8")
        pkt = build_packet(
            src_id=0xA001,
            dst_id=0xFFFF,  # 廣播
            hop=3,
            seq=i + 1,
            pkt_type=0x01,  # 文字
            payload=payload
        )
        protocol.received.clear()
        transport.sendto(wrap_data(pkt))
        print(f"  → 發送文字: \"{msg}\" (SEQ={i+1}, {len(payload)}B)")

        try:
            await asyncio.wait_for(protocol.received.wait(), timeout=3.0)
        except asyncio.TimeoutError:
            print(f"  ⚠ 超時未收到回覆")

        await asyncio.sleep(0.5)

    transport.close()
    print(f"\n=== 文字測試完成 ===")


async def run_voice_test(host: str, port: int):
    """模擬語音封包流（每 200ms 送一包 60B）"""
    loop = asyncio.get_event_loop()
    transport, protocol = await loop.create_datagram_endpoint(
        TestClient, remote_addr=(host, port)
    )

    print(f"=== 語音串流測試 ===")
    print(f"目標: {host}:{port}")
    print(f"模擬 Codec2 @ 2400bps: 每 200ms 送 60 bytes")
    print(f"持續 2 秒（10 個封包）")
    print()

    for i in range(10):
        # 模擬 Codec2 編碼後的 60 bytes（10 幀 × 6B/幀）
        fake_voice = bytes([0x55 ^ (i & 0xFF)] * 60)
        pkt = build_packet(
            src_id=0xA001,
            dst_id=0xFFFF,
            hop=3,
            seq=100 + i,
            pkt_type=0x02,  # 語音
            payload=fake_voice
        )
        protocol.received.clear()
        transport.sendto(wrap_data(pkt))
        print(f"  → 語音包 #{i+1}/10 (SEQ={100+i}, 60B payload)")

        try:
            await asyncio.wait_for(protocol.received.wait(), timeout=2.0)
        except asyncio.TimeoutError:
            print(f"  ⚠ 未收到回覆（可能是 delay 設定較大）")

        await asyncio.sleep(0.2)  # 200ms 間隔

    transport.close()
    print(f"\n=== 語音測試完成 ===")


def main():
    parser = argparse.ArgumentParser(
        description="C6L Mock Server 測試客戶端"
    )
    parser.add_argument("--host", default="127.0.0.1", help="伺服器位址")
    parser.add_argument("--port", type=int, default=5000, help="UDP 埠號")
    parser.add_argument(
        "--mode", choices=["text", "voice", "both"], default="both",
        help="測試模式"
    )
    args = parser.parse_args()

    if args.mode in ("text", "both"):
        asyncio.run(run_text_test(args.host, args.port))
        print()

    if args.mode in ("voice", "both"):
        asyncio.run(run_voice_test(args.host, args.port))


if __name__ == "__main__":
    main()
