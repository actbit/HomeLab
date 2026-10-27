#pragma once

#include <Arduino.h>
#include <WiFi.h>

/**
 * WiFi接続管理
 */
class WifiManager {
private:
    static uint8_t _retryCount;
    static constexpr uint8_t MAX_RETRY_COUNT = 5;

public:
    /**
     * WiFiに接続
     * @param ssid SSID
     * @param password パスワード
     * @param timeoutMs タイムアウト (ms)
     * @return 接続成功
     */
    static bool connect(const String& ssid, const String& password, uint32_t timeoutMs = 15000) {
        Serial.printf("[WiFi] Connecting to %s... (attempt %d)\n", ssid.c_str(), _retryCount + 1);

        WiFi.mode(WIFI_STA);
        WiFi.begin(ssid.c_str(), password.c_str());

        uint32_t start = millis();
        while (WiFi.status() != WL_CONNECTED) {
            if (millis() - start > timeoutMs) {
                Serial.println("[WiFi] Connection timeout");

                // 指数的バックオフ計算
                uint32_t backoffMs = min(5000 * (1 << min(_retryCount, MAX_RETRY_COUNT)), 60000);
                _retryCount++;

                Serial.printf("[WiFi] Will retry in %ums (attempt %d, max backoff 60s)\n",
                              backoffMs, _retryCount);
                return false;
            }
            delay(500);
            Serial.print(".");
        }

        Serial.printf("\n[WiFi] Connected! IP: %s, RSSI: %d\n",
                      WiFi.localIP().toString().c_str(), WiFi.RSSI());

        // 成功時にリトライカウントをリセット
        _retryCount = 0;
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

    /**
     * 現在のリトライ回数を取得
     */
    static uint8_t getRetryCount() {
        return _retryCount;
    }

    /**
     * リトライカウントをリセット
     */
    static void resetRetryCount() {
        _retryCount = 0;
    }
};

// 静的メンバーの初期化
uint8_t WifiManager::_retryCount = 0;
