#include "power_mgr.h"
#include <Arduino.h>

namespace PowerMgr {

// 通話結束後 3 秒無活動 → 退回待機
static const uint32_t IDLE_TIMEOUT_MS = 3000;

static State             _state     = State::STANDBY;
static uint32_t          _lastActMs = 0;
static RadioDutyCycleFunc _dutyCycleFn;

void init(RadioDutyCycleFunc fn) {
    _dutyCycleFn = fn;
    // 初始進入待機模式：啟用 RxDutyCycle 省電
    _dutyCycleFn(true);
    Serial.println("[Power] 初始化完成，進入待機模式（RxDutyCycle）");
}

void onActivity() {
    _lastActMs = millis();
    if (_state == State::STANDBY) {
        _state = State::ACTIVE;
        // 切換到通話模式：關閉 RxDutyCycle，切為連續接收
        _dutyCycleFn(false);
        Serial.println("[Power] → 通話模式（連續 RX）");
    }
}

void loop() {
    if (_state != State::ACTIVE) return;
    if (millis() - _lastActMs > IDLE_TIMEOUT_MS) {
        _state = State::STANDBY;
        // 回到待機模式：啟用 RxDutyCycle
        _dutyCycleFn(true);
        Serial.println("[Power] → 待機模式（RxDutyCycle）");
    }
}

State getState() { return _state; }

} // namespace PowerMgr
