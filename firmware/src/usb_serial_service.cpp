#include "usb_serial_service.h"

UsbSerialService usbSerialService;

void UsbSerialService::begin() {
    // USB CDC 由 build_flags 的 ARDUINO_USB_CDC_ON_BOOT=1 自動啟動
    // Serial 即為 USB CDC，無需額外初始化
    Serial.begin(115200);
    Serial.println("[USB] USB Serial CDC 已啟動");
}

bool UsbSerialService::isConnected() {
    // ESP32-C6 HWCDC 用 operator bool() 判斷 USB 連線狀態
    return (bool)Serial;
}

void UsbSerialService::send(const uint8_t* data, size_t len) {
    if (!isConnected()) return;
    // 先寫入 2-byte 長度前綴，讓接收端知道封包邊界
    uint8_t lenBuf[2] = { (uint8_t)(len >> 8), (uint8_t)(len & 0xFF) };
    Serial.write(lenBuf, 2);
    Serial.write(data, len);
    Serial.flush();
}

void UsbSerialService::onReceive(std::function<void(const uint8_t*, size_t)> cb) {
    _rxCallback = cb;
}

void UsbSerialService::loop() {
    // 簡易幀解析：[長度高8bit][長度低8bit][資料...]
    static uint8_t buf[256];
    static size_t  expected = 0;
    static size_t  received = 0;
    static bool    readingLen = true;
    static uint8_t lenBuf[2];
    static int     lenIdx = 0;

    while (Serial.available()) {
        if (readingLen) {
            lenBuf[lenIdx++] = Serial.read();
            if (lenIdx == 2) {
                expected  = ((size_t)lenBuf[0] << 8) | lenBuf[1];
                received  = 0;
                lenIdx    = 0;
                readingLen = false;
                if (expected == 0 || expected > 255) {
                    // 長度異常，重置狀態
                    readingLen = true;
                }
            }
        } else {
            buf[received++] = Serial.read();
            if (received >= expected) {
                if (_rxCallback) _rxCallback(buf, expected);
                readingLen = true;
            }
        }
    }
}
