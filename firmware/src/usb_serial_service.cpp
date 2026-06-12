#include "usb_serial_service.h"

UsbSerialService usbSerialService;

void UsbSerialService::begin() {
    // USB CDC 由 build_flags 的 ARDUINO_USB_CDC_ON_BOOT=1 自動啟動
    // Serial 即為 USB CDC，無需額外初始化
    Serial.begin(115200);
    Serial.setTxTimeoutMs(0); // 再次確保（Serial.begin 可能重置逾時設定）
    Serial.println("[USB] USB Serial CDC 已啟動");
}

bool UsbSerialService::isConnected() {
    // ESP32-C6 HWCDC 用 operator bool() 判斷 USB 連線狀態
    return (bool)Serial;
}

void UsbSerialService::send(const uint8_t* data, size_t len) {
    // 注意：不可用 isConnected()(=(bool)Serial=DTR) 當守衛——Android USB host
    // 不一定拉起 DTR，會導致「收得到 hello、卻不回 ack」、手機卡在握手中。
    //
    // 但也不能沿用全域 setTxTimeoutMs(0)：timeout=0 在 host 沒「即時就緒」時會
    // 把寫入直接丟棄——ack 就是這樣被丟掉、手機永遠卡握手中（F-054 半通主因）。
    // send() 只在「對方是已握手 APP」時被呼叫（握手 ack，或 broadcastToApps
    // 推給已握手傳輸層的 LoRa 幀），這些「資料」必須確實送達，故寫入期間給短
    // 逾時讓它真的送出；寫完還原為 0，保留「power-only 純供電時 debug log 不卡
    // main loop」的保護（那才是當初設 0 的目的）。
    uint8_t lenBuf[2] = { (uint8_t)(len >> 8), (uint8_t)(len & 0xFF) };
    Serial.setTxTimeoutMs(50);
    Serial.write(lenBuf, 2);
    Serial.write(data, len);
    Serial.flush();
    Serial.setTxTimeoutMs(0);
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
