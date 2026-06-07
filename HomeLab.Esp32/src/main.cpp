/**
 * HomeLab SESAME スマートロックコントローラ - ESP32 ファームウェア
 *
 * 動作モード:
 * 1. Provisioning Mode - 初回設定時、BLE GATT Serverとして動作
 *    Web BLE経由でWiFi/SESAME/サーバー設定を受け取る
 *
 * 2. Operational Mode - 通常運用時
 *    WiFi接続 → WebSocket接続 → ハートビート送信 → コマンド待受
 *    コマンド受信 → SESAME BLE制御 → 結果をWebSocketで通知
 *
 * 通信: WebSocket (MQTT Broker不要・サーバー内蔵)
 * SESAME制御: libsesame3bt を使用
 */

#include <Arduino.h>
#include "config.h"
#include "wifi_manager.h"
#include "websocket_client.h"
#include "sesame_ble.h"
#include "ble_provisioning.h"
#include "crypto.h"
#include <libsesame3bt/SesameScanner.h>

// グローバルインスタンス
Config::DeviceConfig deviceConfig;
WebSocketClient wsClient;
SesameBle sesame;
BleProvisioning bleProv;

// ハートビート間隔
constexpr uint32_t HEARTBEAT_INTERVAL_MS = 30000;  // 30秒

unsigned long lastHeartbeat = 0;
bool operationalMode = false;

// SESAMEのBLE MACアドレスをUUIDから解決
String resolveSesameAddress(const String& uuid) {
    using libsesame3bt::SesameScanner;
    using libsesame3bt::SesameInfo;

    SesameScanner& scanner = SesameScanner::get();
    String result;

    Serial.printf("[SESAME] Scanning for UUID: %s\n", uuid.c_str());

    scanner.scan(10000, [&uuid, &result](SesameScanner& /*scanner*/, const SesameInfo* info) {
        if (info && info->uuid.toString() == uuid.c_str()) {
            result = info->address.toString().c_str();
            Serial.printf("[SESAME] Found! Address: %s, Model: %d\n",
                          result.c_str(), static_cast<int>(info->model));
        }
    });

    return result;
}

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
        Serial.println("[MAIN] Configured, entering operational mode");
        startOperationalMode();
    } else {
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
    if (bleProv.isConfigured()) {
        Serial.println("[MAIN] Provisioning complete, restarting...");
        bleProv.stop();
        delay(1000);

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

    // WebSocket接続 (サーバーに内蔵・MQTT Broker不要)
    wsClient.begin(
        deviceConfig.mqttBrokerUrl,     // サーバーホスト
        deviceConfig.mqttBrokerPort.toInt(),  // サーバーポート
        deviceConfig.deviceId,           // デバイス識別子
        deviceConfig.activationKey       // 認証キー
    );

    // コマンドコールバック設定
    wsClient.onCommand([](const String& action, const String& requestId) {
        handleCommand(action, requestId);
    });

    // SESAME初期化
    String sesameAddr = resolveSesameAddress(deviceConfig.sesameUuid);
    if (sesameAddr.length() > 0) {
        sesame.begin(sesameAddr, deviceConfig.sesameApiKey);
        Serial.println("[MAIN] SESAME initialized");
    } else {
        Serial.println("[MAIN] WARNING: SESAME device not found during scan");
        sesame.begin(deviceConfig.sesameUuid, deviceConfig.sesameApiKey);
    }

    lastHeartbeat = millis();
}

void operationalLoop() {
    // WiFi接続確認
    if (!WifiManager::isConnected()) {
        Serial.println("[MAIN] WiFi disconnected, reconnecting...");
        WifiManager::connect(deviceConfig.wifiSsid, deviceConfig.wifiPassword);
    }

    // WebSocketループ
    wsClient.loop();

    // SESAME ループ (BLEイベント処理)
    sesame.loop();

    // ハートビート送信
    if (millis() - lastHeartbeat >= HEARTBEAT_INTERVAL_MS) {
        lastHeartbeat = millis();

        if (wsClient.isConnected()) {
            wsClient.publishHeartbeat(
                sesame.isLocked(),
                sesame.getBatteryLevel(),
                WifiManager::getRssi()
            );
        }

        // 定期的にSESAMEのステータス更新
        sesame.updateStatus();
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
            wsClient.publishStatus("error", requestId, false, "SESAME BLE connection failed");
            return;
        }
    }

    if (action == "unlock") {
        success = sesame.unlock("HomeLock");
        state = "unlocked";
    } else if (action == "lock") {
        success = sesame.lock("HomeLock");
        state = "locked";
    } else if (action == "toggle") {
        success = sesame.toggle("HomeLock");
        state = sesame.isLocked() ? "locked" : "unlocked";
    } else if (action == "status") {
        sesame.updateStatus();
        state = sesame.isLocked() ? "locked" : "unlocked";
        success = true;
    } else {
        wsClient.publishStatus("error", requestId, false, "Unknown action: " + action);
        return;
    }

    // 結果をWebSocketで通知
    wsClient.publishStatus(state, requestId, success);

    Serial.printf("[CMD] Result: %s, Success: %s\n", state.c_str(), success ? "true" : "false");
}
