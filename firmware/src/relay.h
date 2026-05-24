#pragma once
#include "packet.h"
#include <functional>

// 去重快取大小（ring buffer）
#define RELAY_DEDUP_SIZE 64

using LoRaSendFunc = std::function<bool(const uint8_t*, size_t)>;

/// @brief Mesh 中繼模組：去重 + 多跳轉發
class RelayHandler {
public:
    void init(uint16_t myId, LoRaSendFunc sendFn);

    /// @brief 收到 LoRa 封包後呼叫，決定是否轉發
    /// @return true = 這個封包是給自己的，應交給手機
    bool process(LoRaPacket& pkt);

    void setEnabled(bool en) { _enabled = en; }
    bool isEnabled()  const  { return _enabled; }

private:
    bool _isDuplicate(uint16_t srcId, uint16_t seq);
    void _markSeen(uint16_t srcId, uint16_t seq);

    uint16_t    _myId   = 0;
    bool        _enabled = true;
    LoRaSendFunc _sendFn;

    // 去重快取（ring buffer）
    struct SeenEntry { uint16_t srcId; uint16_t seq; };
    SeenEntry _seenBuf[RELAY_DEDUP_SIZE];
    int       _seenHead = 0;
};

extern RelayHandler relayHandler;
