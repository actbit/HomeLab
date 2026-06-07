#pragma once

#include <Arduino.h>
#include <libsesame3bt/Sesame.h>
#include <libsesame3bt/SesameClient.h>
#include <libsesame3bt/SesameScanner.h>

/**
 * SESAME スマートロック BLE制御
 * libsesame3bt を使用してSESAMEデバイスに直接BLE接続し施錠/解錠を行う
 *
 * 対応デバイス:
 * - SESAME 3 / 4 / 5 / 5 PRO
 * - SESAME bot / bot 2
 * - SESAME 3 bike (Cycle)
 */
class SesameBle {
public:
    /**
     * 初期化
     * @param deviceAddress SESAMEのBLE MACアドレス (例: "xx:xx:xx:xx:xx:xx")
     * @param secretKey SESAMEのシークレットキー (API有効化キーから取得)
     * @param model SESAMEモデル種別
     */
    void begin(const String& deviceAddress, const String& secretKey,
               libsesame3bt::Sesame::model_t model = libsesame3bt::Sesame::model_t::sesame_5) {
        _secretKey = secretKey;
        _model = model;

        // BLEアドレス設定
        _address = BLEAddress(deviceAddress.c_str(), BLE_ADDR_RANDOM);
        Serial.printf("[SESAME] Initialized for %s (model=%d)\n",
                      deviceAddress.c_str(), static_cast<int>(model));
    }

    /**
     * SESAMEにBLE接続
     */
    bool connect() {
        if (_connected) {
            Serial.println("[SESAME] Already connected");
            return true;
        }

        Serial.println("[SESAME] Connecting...");

        _client.begin(_address, _model);
        _client.set_keys("", _secretKey.c_str());

        if (!_client.connect()) {
            Serial.println("[SESAME] Connection failed");
            return false;
        }

        // 接続完了待ち (ステータス受信で判定)
        unsigned long start = millis();
        while (!_client.isReady() && millis() - start < 10000) {
            _client.loop();
            delay(50);
        }

        if (!_client.isReady()) {
            Serial.println("[SESAME] Connection timeout");
            disconnect();
            return false;
        }

        _connected = true;

        // 現在のロック状態を取得
        auto status = _client.getStatus();
        if (status.has_value()) {
            _isLocked = (status.value() == libsesame3bt::Sesame::status_t::locked);
            Serial.printf("[SESAME] Connected! Status: %s\n",
                          _isLocked ? "LOCKED" : "UNLOCKED");
        }

        return true;
    }

    /**
     * 解錠
     * @param tag 操作履歴に記録されるタグ (ユーザー名等)
     */
    bool unlock(const char* tag = "HomeLock") {
        if (!_connected) {
            Serial.println("[SESAME] Not connected");
            return false;
        }

        Serial.printf("[SESAME] Unlocking... (tag: %s)\n", tag);
        bool result = _client.unlock(tag);
        if (result) {
            _isLocked = false;
            Serial.println("[SESAME] Unlock successful");
        } else {
            Serial.println("[SESAME] Unlock failed");
        }
        return result;
    }

    /**
     * 施錠
     * @param tag 操作履歴に記録されるタグ
     */
    bool lock(const char* tag = "HomeLock") {
        if (!_connected) {
            Serial.println("[SESAME] Not connected");
            return false;
        }

        Serial.printf("[SESAME] Locking... (tag: %s)\n", tag);
        bool result = _client.lock(tag);
        if (result) {
            _isLocked = true;
            Serial.println("[SESAME] Lock successful");
        } else {
            Serial.println("[SESAME] Lock failed");
        }
        return result;
    }

    /**
     * トグル (施錠↔解錠)
     */
    bool toggle(const char* tag = "HomeLock") {
        return _isLocked ? unlock(tag) : lock(tag);
    }

    /**
     * 接続切断
     */
    void disconnect() {
        if (_connected) {
            _client.disconnect();
            _connected = false;
            Serial.println("[SESAME] Disconnected");
        }
    }

    /**
     * ループ処理 (メインループで呼び出し)
     * BLEイベント処理を行う
     */
    void loop() {
        if (_connected) {
            _client.loop();
        }
    }

    /**
     * 接続状態取得
     */
    bool isConnected() const { return _connected; }

    /**
     * ロック状態取得
     */
    bool isLocked() const { return _isLocked; }

    /**
     * バッテリーレベル取得 (%)
     */
    int getBatteryLevel() const {
        if (!_connected) return -1;
        auto batt = _client.getBatteryVoltage();
        if (batt.has_value()) {
            // CR2032電圧 3.0V=100%, 2.0V=0% の簡易換算
            int pct = static_cast<int>((batt.value() - 2.0f) / 1.0f * 100.0f);
            return constrain(pct, 0, 100);
        }
        return -1;
    }

    /**
     * ステータス更新 (接続中に状態変化があった場合)
     */
    void updateStatus() {
        if (!_connected) return;
        auto status = _client.getStatus();
        if (status.has_value()) {
            _isLocked = (status.value() == libsesame3bt::Sesame::status_t::locked);
        }
    }

private:
    libsesame3bt::SesameClient _client;
    BLEAddress _address{""};
    String _secretKey;
    libsesame3bt::Sesame::model_t _model = libsesame3bt::Sesame::model_t::sesame_5;
    bool _connected = false;
    bool _isLocked = true;
};
