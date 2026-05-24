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

private:
    std::function<void(const uint8_t*, size_t)> _rxCallback;
    WiFiUDP _udp;
    IPAddress _clientIp;     // 最後一次傳入封包的來源 IP（回傳用）
    uint16_t  _clientPort = 0;
    bool      _clientKnown = false;

    char _ssid[32] = "LoRaPTT";
    char _pass[32] = "loraptt2026";
};

extern WiFiService wifiService;
