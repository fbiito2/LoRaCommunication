#include "relay.h"
#include <string.h>

RelayHandler relayHandler;

void RelayHandler::init(uint16_t myId, LoRaSendFunc sendFn) {
    _myId   = myId;
    _sendFn = sendFn;
    memset(_seenBuf, 0, sizeof(_seenBuf));
    Serial.printf("[Relay] 初始化，裝置 ID: 0x%04X\n", _myId);
}

bool RelayHandler::process(LoRaPacket& pkt) {
    // 是否交給本機 APP：給自己 / 全體廣播 / 群組（群組成員資格由 APP 端判斷）
    bool forMe = (pkt.dstId == _myId) ||
                 dstIsBroadcast(pkt.dstId) ||
                 dstIsGroup(pkt.dstId);

    // 去重檢查：避免重複處理或迴圈轉發
    if (_isDuplicate(pkt.srcId, pkt.seq)) {
        Serial.printf("[Relay] 重複封包丟棄 SRC=0x%04X SEQ=%d\n",
                      pkt.srcId, pkt.seq);
        return false;
    }
    _markSeen(pkt.srcId, pkt.seq);

    // 中繼轉發：所有節點恆為端點 + 中繼。
    // 只要還有跳數、目標不是自己、且非自己發出的封包就轉發。
    bool shouldRelay = (pkt.hop > 0) &&
                       (pkt.dstId != _myId) &&
                       (pkt.srcId != _myId);

    if (shouldRelay) {
        pkt.hop--;
        uint8_t buf[PKT_MAX_LEN];
        size_t len = packetSerialize(pkt, buf, sizeof(buf));
        if (len > 0) {
            _sendFn(buf, len);
            Serial.printf("[Relay] 轉發封包 SRC=0x%04X DST=0x%04X HOP=%d\n",
                          pkt.srcId, pkt.dstId, pkt.hop);
        }
    }

    return forMe;
}

bool RelayHandler::_isDuplicate(uint16_t srcId, uint16_t seq) {
    for (int i = 0; i < RELAY_DEDUP_SIZE; i++) {
        if (_seenBuf[i].srcId == srcId && _seenBuf[i].seq == seq)
            return true;
    }
    return false;
}

void RelayHandler::_markSeen(uint16_t srcId, uint16_t seq) {
    _seenBuf[_seenHead] = { srcId, seq };
    _seenHead = (_seenHead + 1) % RELAY_DEDUP_SIZE;
}
