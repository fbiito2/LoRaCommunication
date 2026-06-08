#include "power_mgr.h"
#include <Arduino.h>

namespace PowerMgr {

// Phase 1：可靠性優先，停用 RxDutyCycle，待機亦維持連續接收。
// RxDutyCycle 需喚醒前導碼長度蓋住睡眠窗（1s 週期需 ~4000 symbols），
// 與目前 16-symbol 前導碼不相容，會導致冷開機首包（含 SOS）被睡過去
// →「收不到→不醒→永遠收不到」死結。省電的 RxDutyCycle 調校留待後期
// （需同步加長喚醒前導碼）。改回時設為 1。
#define ENABLE_RX_DUTYCYCLE 0

// 通話結束後 3 秒無活動 → 退回待機
static const uint32_t IDLE_TIMEOUT_MS = 3000;

static State             _state     = State::STANDBY;
static uint32_t          _lastActMs = 0;
static RadioDutyCycleFunc _dutyCycleFn;

void init(RadioDutyCycleFunc fn) {
    _dutyCycleFn = fn;
#if ENABLE_RX_DUTYCYCLE
    // 初始進入待機模式：啟用 RxDutyCycle 省電
    _dutyCycleFn(true);
    Serial.println("[Power] 初始化完成，進入待機模式（RxDutyCycle）");
#else
    // Phase 1：連續接收，保證收得到（含冷開機首個 SOS）
    _dutyCycleFn(false);
    Serial.println("[Power] 初始化完成，連續接收模式（Phase 1 可靠性優先）");
#endif
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
#if ENABLE_RX_DUTYCYCLE
        // 回到待機模式：啟用 RxDutyCycle
        _dutyCycleFn(true);
        Serial.println("[Power] → 待機模式（RxDutyCycle）");
#else
        // Phase 1：維持連續接收（不切 duty cycle）
        _dutyCycleFn(false);
#endif
    }
}

State getState() { return _state; }

} // namespace PowerMgr
