#pragma once
#include "comm_interface.h"

/// @brief 攜帶模式：USB Serial CDC（手機 USB-C 直連）
class UsbSerialService : public ICommInterface {
public:
    void begin() override;
    void send(const uint8_t* data, size_t len) override;
    void onReceive(std::function<void(const uint8_t*, size_t)> cb) override;
    bool isConnected() override;
    void loop(); // 在 main loop() 呼叫，讀取 Serial 緩衝區

private:
    std::function<void(const uint8_t*, size_t)> _rxCallback;
    // DTR 訊號為 true 代表手機已連上並開啟 Serial port
    bool _dtrDetected = false;
};

extern UsbSerialService usbSerialService;
