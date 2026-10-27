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
#include <SesameScanner.h>

// グローバルインスタンス
Config::DeviceConfig deviceConfig;
WebSocketClient wsClient;
SesameBle sesame;
BleProvisioning bleProv;

// ハートビート間隔
constexpr uint32_t HEARTBEAT_INTERVAL_MS = 30000;  // 30秒

unsigned long lastHeartbeat = 0;
unsigned long lastSesameScan = 0;
bool operationalMode = false;

void startProvisioningMode();
void provisioningLoop();
void startOperationalMode();
void operationalLoop();
void handleCommand(const String& action, const String& requestId);

// SESAMEのBLE MACアドレスをUUIDから解決
String resolveSesameAddress(const String& uuid, libsesame3bt::Sesame::model_t& model, uint8_t& addressType) {
    using libsesame3bt::SesameScanner;
    using libsesame3bt::SesameInfo;

    SesameScanner& scanner = SesameScanner::get();
    String result;

    Serial.printf("[SESAME] Scanning for UUID: %s\n", uuid.c_str());

    scanner.scan(10000, [&uuid, &result, &model, &addressType](SesameScanner& /*scanner*/, const SesameInfo* info) {
        if (info && info->uuid.toString() == uuid.c_str()) {
            result = info->address.toString().c_str();
            model = info->model;
            addressType = info->address.getType();
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
        if (!Crypto::generateKeyPair(deviceConfig.publicKey, deviceConfig.privateKey)) {
            Serial.println("[MAIN] Key generation failed; restarting");
            delay(1000);
            ESP.restart();
            return;
        }
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

    // WiFi接続（指数的バックオフはWifiManager内で処理）
    uint8_t retryCount = 0;
    while (!WifiManager::connect(deviceConfig.wifiSsid, deviceConfig.wifiPassword)) {
        // 接続失敗時のバックオフ時間を取得
        uint8_t currentRetry = WifiManager::getRetryCount();
        uint32_t backoffMs = min<uint32_t>(5000u * (1u << min<uint8_t>(currentRetry, 5)), 60000u);

        Serial.printf("[MAIN] WiFi connection failed, waiting %ums before retry...\n", backoffMs);
        delay(backoffMs);

        // 最大リトライ回数を超えたらリスタート
        if (++retryCount >= 10) {
            Serial.println("[MAIN] Max retries reached, restarting...");
            ESP.restart();
        }
    }

    // WebSocket接続 (サーバーに内蔵・MQTT Broker不要)
    if (deviceConfig.serverUrl.length() > 0) {
        // serverUrlを使用 (ws://host:port)
        wsClient.begin(
            deviceConfig.serverUrl,
            deviceConfig.deviceId,
            deviceConfig.activationKey
        );
        Serial.printf("[MAIN] Using server URL: %s\n", deviceConfig.serverUrl.c_str());
    } else {
        // 互換性: 古い設定形式 (mqttBrokerUrl + mqttBrokerPort)
        wsClient.begin(
            deviceConfig.mqttBrokerUrl,
            deviceConfig.mqttBrokerPort.toInt(),
            deviceConfig.deviceId,
            deviceConfig.activationKey
        );
        Serial.printf("[MAIN] Using legacy format: %s:%s\n",
                      deviceConfig.mqttBrokerUrl.c_str(), deviceConfig.mqttBrokerPort.c_str());
    }

    // コマンドコールバック設定
    wsClient.onCommand([](const String& action, const String& requestId) {
        handleCommand(action, requestId);
    });

    // SESAME初期化
    NimBLEDevice::init("");
    auto sesameModel = libsesame3bt::Sesame::model_t::sesame_5;
    uint8_t addressType = BLE_ADDR_RANDOM;
    String sesameAddr = resolveSesameAddress(deviceConfig.sesameUuid, sesameModel, addressType);
    lastSesameScan = millis();
    if (sesameAddr.length() > 0) {
        sesame.begin(sesameAddr, deviceConfig.sesameApiKey, sesameModel, addressType);
        Serial.println("[MAIN] SESAME initialized");
    } else {
        Serial.println("[MAIN] WARNING: SESAME device not found during scan");
        // UUID is not a BLE MAC address. Retry scanning rather than connecting
        // to an invalid address or reporting a guessed lock state.
    }

    lastHeartbeat = millis();
}

void operationalLoop() {
    // WiFi接続確認
    if (!WifiManager::isConnected()) {
        Serial.println("[MAIN] WiFi disconnected, attempting to reconnect...");
        if (!WifiManager::connect(deviceConfig.wifiSsid, deviceConfig.wifiPassword)) {
            // 接続失敗時はバックオフ待機
            uint8_t currentRetry = WifiManager::getRetryCount();
            uint32_t backoffMs = min<uint32_t>(5000u * (1u << min<uint8_t>(currentRetry, 5)), 60000u);
            Serial.printf("[MAIN] Waiting %ums before retry...\n", backoffMs);
            delay(backoffMs);
            return; // 次のループで再試行
        }
        // WiFi再接続成功後、WebSocketも再接続が必要
        Serial.println("[MAIN] WiFi reconnected, restarting...");
        delay(1000);
        ESP.restart();
    }

    // WebSocketループ
    wsClient.loop();

    // SESAME ループ (BLEイベント処理)
    sesame.loop();
    if (!sesame.isInitialized() && millis() - lastSesameScan >= 60000) {
        lastSesameScan = millis();
        auto model = libsesame3bt::Sesame::model_t::sesame_5;
        uint8_t addressType = BLE_ADDR_RANDOM;
        String address = resolveSesameAddress(deviceConfig.sesameUuid, model, addressType);
        if (address.length() > 0) sesame.begin(address, deviceConfig.sesameApiKey, model, addressType);
    }
    if (!sesame.isConnected()) sesame.ensureConnected();

    // ハートビート送信
    if (millis() - lastHeartbeat >= HEARTBEAT_INTERVAL_MS) {
        lastHeartbeat = millis();

        if (wsClient.isConnected()) {
            wsClient.publishHeartbeat(
                sesame.isLocked(),
                sesame.hasStatus(),
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

    // SESAMEにBLE接続を確保（ensureConnectedで管理）
    if (!sesame.ensureConnected()) {
        wsClient.publishStatus("error", requestId, false, "SESAME BLE connection failed");
        return;
    }

    if ((action == "toggle") && !sesame.hasStatus()) {
        wsClient.publishStatus("unknown", requestId, false, "SESAME state is not known");
        return;
    }

    const bool targetLocked = action == "lock" || (action == "toggle" && !sesame.isLocked());
    const uint32_t previousRevision = sesame.statusRevision();
    if (action == "unlock") {
        success = sesame.unlock("HomeLock");
    } else if (action == "lock") {
        success = sesame.lock("HomeLock");
    } else if (action == "toggle") {
        success = sesame.toggle("HomeLock");
    } else if (action == "status") {
        sesame.updateStatus();
        success = true;
    } else {
        wsClient.publishStatus("error", requestId, false, "Unknown action: " + action);
        return;
    }

    // 送信成功だけでは施錠成功と判断せず、通知された実際の状態を確認する。
    if (success) {
        const unsigned long start = millis();
        unsigned long lastRequest = 0;
        success = false;
        while (millis() - start < 15000) {
            if (sesame.statusRevision() != previousRevision && sesame.hasStatus() &&
                (action == "status" || sesame.isLocked() == targetLocked)) {
                success = true;
                break;
            }
            if (millis() - lastRequest >= 1000) {
                sesame.updateStatus();
                lastRequest = millis();
            }
            delay(50);
        }
    }
    state = success ? (sesame.isLocked() ? "locked" : "unlocked") : "unknown";
    wsClient.publishStatus(state, requestId, success, success ? "" : "SESAME state confirmation failed");

    Serial.printf("[CMD] Result: %s, Success: %s\n", state.c_str(), success ? "true" : "false");
}
