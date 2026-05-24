#pragma once
#include <functional>

using RadioDutyCycleFunc = std::function<void(bool enable)>;

/// @brief 電源狀態機：待機（省電）↔ 通話（全速）
namespace PowerMgr {
    enum class State { STANDBY, ACTIVE };

    void init(RadioDutyCycleFunc dutyCycleFn);

    /// @brief 收到封包或 PTT 按下時呼叫 → 切換到通話模式
    void onActivity();

    /// @brief 在 main loop() 呼叫，處理超時退回待機
    void loop();

    State getState();
}
