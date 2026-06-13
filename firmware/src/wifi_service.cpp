#include "wifi_service.h"

WiFiService wifiService;

void WiFiService::setSsid(const char* ssid) { strncpy(_ssid, ssid, 31); }
void WiFiService::setPassword(const char* pass) { strncpy(_pass, pass, 31); }

void WiFiService::begin() {
    setApEnabled(true);
}

void WiFiService::setApEnabled(bool enable) {
    if (enable == _apEnabled) return;

    if (enable) {
        // 開啟 WiFi AP 熱點模式
        WiFi.mode(WIFI_AP);
        WiFi.softAP(_ssid, _pass);
        IPAddress ip = WiFi.softAPIP();
        Serial.printf("[WiFi] AP 已啟動，SSID: %s，IP: %s\n",
                      _ssid, ip.toString().c_str());
        _udp.begin(WIFI_UDP_PORT);
        Serial.printf("[WiFi] UDP Server 監聽 port %d\n", WIFI_UDP_PORT);
    } else {
        _udp.stop();
        WiFi.softAPdisconnect(true);
        WiFi.mode(WIFI_OFF);
        for (auto& c : _clients) c.port = 0; // 清空 client 清單
        Serial.println("[WiFi] AP 已關閉（省電）");
    }
    _apEnabled = enable;
}

bool WiFiService::isConnected() {
    uint32_t now = millis();
    for (auto& c : _clients)
        if (c.port != 0 && now - c.lastMs <= CLIENT_TTL_MS) return true;
    return false;
}

// 記錄/更新來源 client：已存在則更新時間；否則用空位，滿了淘汰最舊
void WiFiService::_touchClient(IPAddress ip, uint16_t port) {
    uint32_t now = millis();
    int freeSlot = -1, oldest = 0;
    for (int i = 0; i < MAX_CLIENTS; i++) {
        if (_clients[i].port == port && _clients[i].ip == ip) {
            _clients[i].lastMs = now; return; // 已知 client → 更新活動時間
        }
        if (_clients[i].port == 0 && freeSlot < 0) freeSlot = i;
        if (_clients[i].lastMs < _clients[oldest].lastMs) oldest = i;
    }
    int slot = (freeSlot >= 0) ? freeSlot : oldest;
    _clients[slot].ip = ip; _clients[slot].port = port; _clients[slot].lastMs = now;
}

void WiFiService::send(const uint8_t* data, size_t len) {
    uint32_t now = millis();
    for (auto& c : _clients) {
        if (c.port == 0) continue;
        if (now - c.lastMs > CLIENT_TTL_MS) { c.port = 0; continue; } // 過期淘汰
        _udp.beginPacket(c.ip, c.port);
        _udp.write(data, len);
        _udp.endPacket();
    }
}

void WiFiService::onReceive(std::function<void(const uint8_t*, size_t)> cb) {
    _rxCallback = cb;
}

void WiFiService::loop() {
    int pktSize = _udp.parsePacket();
    if (pktSize <= 0) return;

    // 記錄來源 client（多 client：推送時推給清單裡全部，不互相搶）
    _touchClient(_udp.remoteIP(), _udp.remotePort());

    uint8_t buf[256];
    int len = _udp.read(buf, sizeof(buf));
    if (len > 0 && _rxCallback)
        _rxCallback(buf, (size_t)len);
}
