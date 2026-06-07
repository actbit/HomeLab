/**
 * HomeLab SESAME スマートロックコントローラ - ESP32 ファームウェア
 *
 * 動作モード:
 * 1. Provisioning Mode - 初回設定時、BLE GATT Serverとして動作
 *    Web BLE経由でWiFi/SESAME/サーバー設定を受け取る
 *
 * 2. Operational Mode - 通常運用時
 *    WiFi接続 → MQTT接続 → ハートビート送信 → コマンド待受
 *    コマンド受信 → SESAME BLE制御 → 結果をMQTTで通知
 */

#include <Arduino.h>
#include "config.h"
#include "wifi_manager.h"
#include "mqtt_client.h"
#include "sesame_ble.h"
#include "ble_provisioning.h"
#include "crypto.h"

// グローバルインスタンス
Config::DeviceConfig deviceConfig;
MqttClient mqttClient;
SesameBle sesame;
BleProvisioning bleProv;

// ハートビート間隔
constexpr uint32_t HEARTBEAT_INTERVAL_MS = 30000;  // 30秒
constexpr uint32_t COMMAND_TIMEOUT_MS = 10000;      // 10秒

unsigned long lastHeartbeat = 0;
bool operationalMode = false;

/**
 * 初期化
 */
void setup() {
    Serial.begin(115200);
    Serial.println("\n=== HomeLab SESAME Controller ===");
    Serial.printf("Version: %s\n", HOMELAB_VERSION);

    // 設定読み込み
    deviceConfig = Config::load();

    if (deviceConfig.configured) {
        // 設定済み → 運用モード
        Serial.println("[MAIN] Configured, entering operational mode");
        startOperationalMode();
    } else {
        // 未設定 → プロビジョニングモード
        Serial.println("[MAIN] Not configured, entering provisioning mode");
        startProvisioningMode();
    }
}

/**
 * メインループ
 */
void loop() {
    if (operationalMode) {
        operationalLoop();
    } else {
        provisioningLoop();
    }
}

// ============================================================
// プロビジョニングモード
// ============================================================

void startProvisioningMode() {
    operationalMode = false;

    // 鍵ペア生成 (初回のみ)
    if (deviceConfig.deviceId.length() == 0) {
        Crypto::generateKeyPair(deviceConfig.publicKey, deviceConfig.privateKey);
        deviceConfig.deviceId = Crypto::generateDeviceId();
        Config::save(deviceConfig);
        Serial.printf("[MAIN] Generated device ID: %s\n", deviceConfig.deviceId.c_str());
    }

    // BLE provisioning 開始
    bleProv.begin(deviceConfig);
}

void provisioningLoop() {
    // 設定が完了するまで待機
    if (bleProv.isConfigured()) {
        Serial.println("[MAIN] Provisioning complete, restarting...");
        bleProv.stop();
        delay(1000);

        // 設定を再読込して運用モードへ
        deviceConfig = Config::load();
        startOperationalMode();
    }
    delay(100);
}

// ============================================================
// 運用モード
// ============================================================

void startOperationalMode() {
    operationalMode = true;

    // WiFi接続
    if (!WifiManager::connect(deviceConfig.wifiSsid, deviceConfig.wifiPassword)) {
        Serial.println("[MAIN] WiFi connection failed, restarting in 30s...");
        delay(30000);
        ESP.restart();
    }

    // MQTT接続
    mqttClient.begin(
        deviceConfig.mqttBrokerUrl,
        deviceConfig.mqttBrokerPort.toInt(),
        deviceConfig.deviceId
    );

    // コマンドコールバック設定
    mqttClient.onCommand([](const String& action, const String& requestId) {
        handleCommand(action, requestId);
    });

    // MQTT接続
    // テナントID = アクティベーションキーを使用 (簡易実装)
    // 実際はアクティベーションキーからテナントIDを取得するフローが必要
    if (!mqttClient.connect("pending-tenant")) {
        Serial.println("[MAIN] MQTT connection failed, will retry in loop");
    }

    // SESAME初期化
    sesame.begin(deviceConfig.sesameUuid, deviceConfig.sesameApiKey);

    lastHeartbeat = millis();
}

void operationalLoop() {
    // WiFi接続確認
    if (!WifiManager::isConnected()) {
        Serial.println("[MAIN] WiFi disconnected, reconnecting...");
        WifiManager::connect(deviceConfig.wifiSsid, deviceConfig.wifiPassword);
    }

    // MQTTループ
    mqttClient.loop();

    // ハートビート送信
    if (millis() - lastHeartbeat >= HEARTBEAT_INTERVAL_MS) {
        lastHeartbeat = millis();

        if (mqttClient.isConnected()) {
            mqttClient.publishHeartbeat(
                sesame.isLocked(),
                100,  // バッテリーレベル (ESP32自身、固定値)
                WifiManager::getRssi()
            );
        }
    }

    delay(10);
}

// ============================================================
// コマンド処理
// ============================================================

void handleCommand(const String& action, const String& requestId) {
    Serial.printf("[CMD] Action: %s, RequestId: %s\n", action.c_str(), requestId.c_str());

    bool success = false;
    String state;

    // SESAMEにBLE接続 (必要時のみ)
    if (!sesame.isConnected()) {
        if (!sesame.connect()) {
            mqttClient.publishStatus("error", requestId, false, "SESAME BLE connection failed");
            return;
        }
    }

    if (action == "unlock") {
        success = sesame.unlock();
        state = "unlocked";
    } else if (action == "lock") {
        success = sesame.lock();
        state = "locked";
    } else if (action == "toggle") {
        success = sesame.toggle();
        state = sesame.isLocked() ? "locked" : "unlocked";
    } else if (action == "status") {
        state = sesame.isLocked() ? "locked" : "unlocked";
        success = true;
    } else {
        mqttClient.publishStatus("error", requestId, false, "Unknown action: " + action);
        return;
    }

    // 結果をMQTTで通知
    mqttClient.publishStatus(state, requestId, success);

    Serial.printf("[CMD] Result: %s, Success: %s\n", state.c_str(), success ? "true" : "false");
}
