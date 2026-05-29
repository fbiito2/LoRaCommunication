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

    /// @brief 收到 LoRa 封包後呼叫：去重、必要時轉發，並判斷是否屬於本機
    /// @return true = 這個封包應交給手機 APP（自己/廣播/群組，群組成員資格由 APP 判斷）
    bool process(LoRaPacket& pkt);

private:
    bool _isDuplicate(uint16_t srcId, uint16_t seq);
    void _markSeen(uint16_t srcId, uint16_t seq);

    uint16_t    _myId   = 0;
    LoRaSendFunc _sendFn;

    // 去重快取（ring buffer）
    struct SeenEntry { uint16_t srcId; uint16_t seq; };
    SeenEntry _seenBuf[RELAY_DEDUP_SIZE];
    int       _seenHead = 0;
};

extern RelayHandler relayHandler;
