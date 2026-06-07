#pragma once

#include <Arduino.h>
#include <NimBLEDevice.h>

/**
 * SESAME スマートロック BLE制御
 * SESAMEデバイスに直接BLE接続して施錠/解錠を行う
 */
class SesameBle {
public:
    /**
     * 初期化
     * @param sesameUuid SESAMEデバイスのBLE UUID
     * @param apiKey SESAME API有効化キー
     */
    void begin(const String& sesameUuid, const String& apiKey) {
        _sesameUuid = sesameUuid;
        _apiKey = apiKey;
        _connected = false;
    }

    /**
     * SESAMEにBLE接続
     */
    bool connect() {
        Serial.printf("[SESAME] Connecting to %s...\n", _sesameUuid.c_str());

        NimBLEDevice::init("HomeLock-ESP32");

        NimBLEAdvertisedDevice* device = scanForDevice(_sesameUuid);
        if (!device) {
            Serial.println("[SESAME] Device not found");
            return false;
        }

        NimBLEClient* client = NimBLEDevice::createClient();
        if (!client->connect(device)) {
            Serial.println("[SESAME] Connection failed");
            return false;
        }

        Serial.println("[SESAME] Connected");
        _connected = true;

        // SESAMEのBLEサービス・キャラクタリスティックを検索
        // 注: 実際のSESAMEプロトコルは非公開のため、
        // SESAME SDK/オープンソース実装を参考に実装が必要
        // ここではインターフェースのみ定義

        return true;
    }

    /**
     * 解錠
     */
    bool unlock() {
        if (!_connected) {
            Serial.println("[SESAME] Not connected");
            return false;
        }

        Serial.println("[SESAME] Unlocking...");
        // TODO: SESAME BLE コマンド送信
        // 実際の実装はSESAMEプロトコルに依存
        _isLocked = false;
        return true;
    }

    /**
     * 施錠
     */
    bool lock() {
        if (!_connected) {
            Serial.println("[SESAME] Not connected");
            return false;
        }

        Serial.println("[SESAME] Locking...");
        // TODO: SESAME BLE コマンド送信
        _isLocked = true;
        return true;
    }

    /**
     * トグル (施錠↔解錠)
     */
    bool toggle() {
        return _isLocked ? unlock() : lock();
    }

    /**
     * 接続切断
     */
    void disconnect() {
        if (_connected) {
            NimBLEDevice::deleteAllBonds();
            _connected = false;
            Serial.println("[SESAME] Disconnected");
        }
    }

    bool isConnected() const { return _connected; }
    bool isLocked() const { return _isLocked; }

private:
    String _sesameUuid;
    String _apiKey;
    bool _connected = false;
    bool _isLocked = true;

    /**
     * 指定UUIDのデバイスをスキャン
     */
    NimBLEAdvertisedDevice* scanForDevice(const String& uuid) {
        NimBLEScan* scan = NimBLEDevice::getScan();
        scan->setActiveScan(true);
        scan->setInterval(100);
        scan->setWindow(99);

        NimBLEScanResults results = scan->start(10, false);

        for (int i = 0; i < results.getCount(); i++) {
            NimBLEAdvertisedDevice* device = results.getDevice(i);
            if (device->getServiceUUID() == NimBLEUUID(uuid.c_str())) {
                return device;
            }
        }

        return nullptr;
    }
};
