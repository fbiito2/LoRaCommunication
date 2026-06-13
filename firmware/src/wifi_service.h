#pragma once
#include "comm_interface.h"
#include <WiFi.h>
#include <WiFiUdp.h>

#define WIFI_UDP_PORT 5000

/// @brief 基站模式：WiFi AP 熱點 + UDP Server
class WiFiService : public ICommInterface {
public:
    void begin() override;
    void send(const uint8_t* data, size_t len) override;
    void onReceive(std::function<void(const uint8_t*, size_t)> cb) override;
    bool isConnected() override;
    void loop(); // 在 main loop() 呼叫，處理 UDP 接收

    void setSsid(const char* ssid);
    void setPassword(const char* pass);

    /// @brief 開/關 WiFi AP（F-041 省電切換）；關閉時停止熱點與 UDP
    void setApEnabled(bool enable);
    bool isApEnabled() const { return _apEnabled; }

private:
    std::function<void(const uint8_t*, size_t)> _rxCallback;
    WiFiUDP _udp;

    // 多 client：收到 LoRa 封包推給「所有」已知 client，不再只推最後一個。
    // 否則多支手機/工具連同一台 AP 時會互相搶走推送對象（實測踩過：pc-client
    // 一連就把手機的收訊搶走）。LRU 淘汰最舊、TTL 過期清掉沒活動的。
    static const int      MAX_CLIENTS   = 4;
    static const uint32_t CLIENT_TTL_MS = 300000; // 5 分鐘無活動即淘汰
    struct Client { IPAddress ip; uint16_t port = 0; uint32_t lastMs = 0; };
    Client _clients[MAX_CLIENTS] = {};
    void _touchClient(IPAddress ip, uint16_t port); // 記錄/更新來源 client

    bool      _apEnabled   = false;

    char _ssid[32] = "LoRaPTT";
    char _pass[32] = "loraptt2026";
};

extern WiFiService wifiService;
