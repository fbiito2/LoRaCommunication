#pragma once
#include <Arduino.h>
#include <functional>

/// @brief 通訊抽象介面 — USB Serial 與 WiFi 共用同一套 API
class ICommInterface {
public:
    virtual void begin()   = 0;
    virtual void send(const uint8_t* data, size_t len) = 0;
    virtual void onReceive(std::function<void(const uint8_t*, size_t)> cb) = 0;
    virtual bool isConnected() = 0;
    virtual ~ICommInterface() = default;
};
