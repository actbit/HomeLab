#pragma once

#include <Arduino.h>
#include <WiFi.h>

/**
 * WiFi接続管理
 */
class WifiManager {
public:
    /**
     * WiFiに接続
     * @param ssid SSID
     * @param password パスワード
     * @param timeoutMs タイムアウト (ms)
     * @return 接続成功
     */
    static bool connect(const String& ssid, const String& password, uint32_t timeoutMs = 15000) {
        Serial.printf("[WiFi] Connecting to %s...\n", ssid.c_str());

        WiFi.mode(WIFI_STA);
        WiFi.begin(ssid.c_str(), password.c_str());

        uint32_t start = millis();
        while (WiFi.status() != WL_CONNECTED) {
            if (millis() - start > timeoutMs) {
                Serial.println("[WiFi] Connection timeout");
                return false;
            }
            delay(500);
            Serial.print(".");
        }

        Serial.printf("\n[WiFi] Connected! IP: %s, RSSI: %d\n",
                      WiFi.localIP().toString().c_str(), WiFi.RSSI());
        return true;
    }

    /**
     * WiFi接続状態確認
     */
    static bool isConnected() {
        return WiFi.status() == WL_CONNECTED;
    }

    /**
     * WiFi切断
     */
    static void disconnect() {
        WiFi.disconnect(true);
        WiFi.mode(WIFI_OFF);
    }

    /**
     * RSSI取得
     */
    static int32_t getRssi() {
        return WiFi.RSSI();
    }
};
