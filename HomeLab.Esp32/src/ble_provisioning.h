#pragma once

#include <Arduino.h>
#include <NimBLEDevice.h>
#include <NimBLEServer.h>
#include <NimBLECharacteristic.h>
#include "config.h"

/**
 * BLE プロビジョニングサーバー
 * 初回設定時にWeb BLEから接続して設定を受け取る
 */
class BleProvisioning {
public:
    /**
     * BLE provisioning サーバーを開始
     * 設定が書き込まれたら onConfigured コールバックが呼ばれる
     */
    void begin(Config::DeviceConfig& config) {
        _config = &config;
        _configured = false;

        NimBLEDevice::init("HomeLock-ESP32");
        NimBLEServer* server = NimBLEDevice::createServer();

        NimBLEService* service = server->createService(Config::BLE_SERVICE_UUID);

        // Device ID (Read)
        _deviceIdChar = service->createCharacteristic(
            "0000fe41-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::READ
        );

        // Device Public Key (Read)
        _publicKeyChar = service->createCharacteristic(
            "0000fe42-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::READ
        );

        // WiFi SSID (Write)
        _ssidChar = service->createCharacteristic(
            "0000fe43-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _ssidChar->setCallbacks(this);

        // WiFi Password (Write)
        _wifiPassChar = service->createCharacteristic(
            "0000fe44-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _wifiPassChar->setCallbacks(this);

        // SESAME UUID (Write)
        _sesameUuidChar = service->createCharacteristic(
            "0000fe45-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _sesameUuidChar->setCallbacks(this);

        // SESAME API Key (Write)
        _sesameKeyChar = service->createCharacteristic(
            "0000fe46-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _sesameKeyChar->setCallbacks(this);

        // Activation Key (Write)
        _activationKeyChar = service->createCharacteristic(
            "0000fe47-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _activationKeyChar->setCallbacks(this);

        // MQTT Broker URL (Write)
        _mqttUrlChar = service->createCharacteristic(
            "0000fe48-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::WRITE
        );
        _mqttUrlChar->setCallbacks(this);

        // Status (Notify)
        _statusChar = service->createCharacteristic(
            "0000fe49-8e22-4541-9d4c-21edae82e5a1",
            NIMBLE_PROPERTY::NOTIFY
        );

        // デバイスIDと公開鍵をセット
        _deviceIdChar->setValue(config.deviceId);
        _publicKeyChar->setValue(config.publicKey, sizeof(config.publicKey));

        service->start();

        // 広告開始
        NimBLEAdvertising* advertising = NimBLEDevice::getAdvertising();
        advertising->addServiceUUID(Config::BLE_SERVICE_UUID);
        advertising->setScanResponse(true);
        NimBLEDevice::startAdvertising();

        Serial.println("[BLE] Provisioning server started, waiting for connection...");
    }

    /**
     * 設定完了したか
     */
    bool isConfigured() const { return _configured; }

    /**
     * BLE provisioning を停止
     */
    void stop() {
        NimBLEDevice::stopAdvertising();
        NimBLEDevice::deinit(true);
        Serial.println("[BLE] Provisioning stopped");
    }

    // NimBLECharacteristicCallbacks
    void onWrite(NimBLECharacteristic* pCharacteristic) override {
        String uuid = pCharacteristic->getUUID().toString();
        String value = pCharacteristic->getValue();

        Serial.printf("[BLE] Write to %s: %s\n", uuid.c_str(), value.c_str());

        if (uuid == "0000fe43-8e22-4541-9d4c-21edae82e5a1") {
            _config->wifiSsid = value;
        } else if (uuid == "0000fe44-8e22-4541-9d4c-21edae82e5a1") {
            _config->wifiPassword = value;
        } else if (uuid == "0000fe45-8e22-4541-9d4c-21edae82e5a1") {
            _config->sesameUuid = value;
        } else if (uuid == "0000fe46-8e22-4541-9d4c-21edae82e5a1") {
            _config->sesameApiKey = value;
        } else if (uuid == "0000fe47-8e22-4541-9d4c-21edae82e5a1") {
            _config->activationKey = value;
        } else if (uuid == "0000fe48-8e22-4541-9d4c-21edae82e5a1") {
            _config->mqttBrokerUrl = value;
            // MQTT URLが書き込まれたら全設定完了とみなす
            _config->configured = true;
            _configured = true;
            Config::save(*_config);

            // ステータス通知
            _statusChar->setValue("configured");
            _statusChar->notify();

            Serial.println("[BLE] All settings received, configuration complete!");
        }
    }

private:
    Config::DeviceConfig* _config;
    bool _configured;

    NimBLECharacteristic* _deviceIdChar;
    NimBLECharacteristic* _publicKeyChar;
    NimBLECharacteristic* _ssidChar;
    NimBLECharacteristic* _wifiPassChar;
    NimBLECharacteristic* _sesameUuidChar;
    NimBLECharacteristic* _sesameKeyChar;
    NimBLECharacteristic* _activationKeyChar;
    NimBLECharacteristic* _mqttUrlChar;
    NimBLECharacteristic* _statusChar;
};
