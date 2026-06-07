#pragma once

#include <Arduino.h>
#include <Preferences.h>

/**
 * ESP32 設定管理
 * NVS (Non-Volatile Storage) にWiFi/SESAME/MQTT設定を保存
 */
class Config {
public:
    struct DeviceConfig {
        String deviceId;           // デバイス固有ID
        String wifiSsid;
        String wifiPassword;
        String sesameUuid;         // SESAME BLE デバイスUUID
        String sesameApiKey;       // SESAME API有効化キー
        String activationKey;      // サーバーアクティベーションキー
        String mqttBrokerUrl;      // MQTTブローカーURL
        String mqttBrokerPort;     // MQTTブローカーポート
        uint8_t publicKey[64];     // ECDSA P-256 公開鍵
        uint8_t privateKey[32];    // ECDSA P-256 秘密鍵
        bool configured;           // 初期設定済みフラグ
    };

    static constexpr const char* NVS_NAMESPACE = "homelab";
    static constexpr const char* BLE_SERVICE_UUID = "0000fe40-cc7a-482a-984a-df91a4f35e6a";

    /**
     * 設定をNVSから読み込み
     */
    static DeviceConfig load() {
        Preferences prefs;
        prefs.begin(NVS_NAMESPACE, true);

        DeviceConfig config;
        config.deviceId = prefs.getString("device_id", "");
        config.wifiSsid = prefs.getString("wifi_ssid", "");
        config.wifiPassword = prefs.getString("wifi_pass", "");
        config.sesameUuid = prefs.getString("sesame_uuid", "");
        config.sesameApiKey = prefs.getString("sesame_key", "");
        config.activationKey = prefs.getString("activ_key", "");
        config.mqttBrokerUrl = prefs.getString("mqtt_url", "");
        config.mqttBrokerPort = prefs.getString("mqtt_port", "1883");
        config.configured = prefs.getBool("configured", false);

        size_t pubLen = prefs.getBytes("pub_key", config.publicKey, sizeof(config.publicKey));
        size_t privLen = prefs.getBytes("priv_key", config.privateKey, sizeof(config.privateKey));

        prefs.end();
        return config;
    }

    /**
     * 設定をNVSに保存
     */
    static void save(const DeviceConfig& config) {
        Preferences prefs;
        prefs.begin(NVS_NAMESPACE, false);

        prefs.putString("device_id", config.deviceId);
        prefs.putString("wifi_ssid", config.wifiSsid);
        prefs.putString("wifi_pass", config.wifiPassword);
        prefs.putString("sesame_uuid", config.sesameUuid);
        prefs.putString("sesame_key", config.sesameApiKey);
        prefs.putString("activ_key", config.activationKey);
        prefs.putString("mqtt_url", config.mqttBrokerUrl);
        prefs.putString("mqtt_port", config.mqttBrokerPort);
        prefs.putBytes("pub_key", config.publicKey, sizeof(config.publicKey));
        prefs.putBytes("priv_key", config.privateKey, sizeof(config.privateKey));
        prefs.putBool("configured", config.configured);

        prefs.end();
    }

    /**
     * 設定をクリア (ファクトリーリセット)
     */
    static void clear() {
        Preferences prefs;
        prefs.begin(NVS_NAMESPACE, false);
        prefs.clear();
        prefs.end();
    }
};
