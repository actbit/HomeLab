#pragma once

#include <Arduino.h>
#include <WebSocketsClient.h>
#include <ArduinoJson.h>
#include <time.h>

// Local, untracked deployment configuration with a PEM CA certificate.
#if __has_include("tls_ca.h")
#include "tls_ca.h"
#endif

/**
 * WebSocketクライアント
 * ASP.NET Coreサーバーに直接接続 (MQTT Broker不要)
 *
 * 接続: ws://server:port/ws/device?deviceId=xxx&activationKey=yyy
 * メッセージ形式: JSON
 *   - heartbeat: {"type":"heartbeat","isLocked":true,"batteryLevel":85,"wifiRssi":-42}
 *   - status:    {"type":"status","state":"locked","requestId":"abc","success":true}
 *   - 受信コマンド: {"type":"command","action":"unlock","requestId":"abc"}
 */
class WebSocketClient {
public:
    using CommandCallback = std::function<void(const String& action, const String& requestId)>;

    /// serverUrl から初期化 (ws://host:port)
    void begin(const String& serverUrl, const String& deviceId, const String& activationKey) {
        _deviceId = deviceId;
        _activationKey = activationKey;

        // URL解析: ws://host:port または ws://host
        if (!parseServerUrl(serverUrl)) {
            Serial.printf("[WS] Invalid server URL: %s\n", serverUrl.c_str());
            return;
        }

        String path = "/ws/device";

        if (_secure) {
#ifdef HOMELAB_TLS_CA_CERT
            configTime(0, 0, "pool.ntp.org", "time.google.com");
            const unsigned long started = millis();
            while (time(nullptr) < 1700000000 && millis() - started < 15000) delay(100);
            if (time(nullptr) < 1700000000) {
                Serial.println("[WS] Time sync failed; refusing TLS connection");
                return;
            }
            _ws.beginSslWithCA(_host.c_str(), _port, path.c_str(), HOMELAB_TLS_CA_CERT);
#else
            Serial.println("[WS] Configure a trusted CA in tls_ca.h before using wss://");
            return;
#endif
        } else {
            _ws.begin(_host, _port, path);
        }
        _ws.setReconnectInterval(5000);
        _ws.enableHeartbeat(15000, 3000, 2);

        _ws.onEvent([this](WStype_t type, uint8_t* payload, size_t length) {
            onEvent(type, payload, length);
        });

        Serial.printf("[WS] Connecting to ws://%s:%d%s\n", _host.c_str(), _port, path.c_str());
    }

    /// 互換性: ホストとポートを指定して初期化
    void begin(const String& host, uint16_t port, const String& deviceId, const String& activationKey) {
        _secure = false;
        _host = host;
        _port = port;
        _deviceId = deviceId;
        _activationKey = activationKey;

        String path = "/ws/device";

        _ws.begin(_host, _port, path);
        _ws.setReconnectInterval(5000);
        _ws.enableHeartbeat(15000, 3000, 2);

        _ws.onEvent([this](WStype_t type, uint8_t* payload, size_t length) {
            onEvent(type, payload, length);
        });

        Serial.printf("[WS] Connecting to ws://%s:%d%s\n", _host.c_str(), _port, path.c_str());
    }

    void loop() {
        _ws.loop();
    }

    bool isConnected() {
        return _ws.isConnected();
    }

    void onCommand(CommandCallback callback) {
        _commandCallback = callback;
    }

    /// 認証メッセージ送信（接続成功後に呼び出し）
    void sendAuthMessage() {
        JsonDocument doc;
        doc["deviceId"] = _deviceId;
        doc["activationKey"] = _activationKey;

        String json;
        serializeJson(doc, json);
        _ws.sendTXT(json);
        Serial.printf("[WS] Auth message sent: deviceId=%s\n", _deviceId.c_str());
    }

    /// ハートビート送信
    void publishHeartbeat(bool isLocked, bool stateKnown, int batteryLevel, int wifiRssi) {
        JsonDocument doc;
        doc["type"] = "heartbeat";
        if (stateKnown) doc["isLocked"] = isLocked;
        else doc["isLocked"] = nullptr;
        doc["stateConfirmed"] = stateKnown;
        if (batteryLevel >= 0) doc["batteryLevel"] = batteryLevel;
        doc["wifiRssi"] = wifiRssi;
        doc["timestamp"] = millis();

        String json;
        serializeJson(doc, json);
        _ws.sendTXT(json);
    }

    /// ステータス送信 (コマンド実行結果)
    void publishStatus(const String& state, const String& requestId, bool success, const String& errorMessage = "") {
        JsonDocument doc;
        doc["type"] = "status";
        doc["state"] = state;
        doc["requestId"] = requestId;
        doc["success"] = success;
        doc["stateConfirmed"] = success && (state == "locked" || state == "unlocked");
        if (errorMessage.length() > 0) {
            doc["errorMessage"] = errorMessage;
        }
        doc["timestamp"] = millis();

        String json;
        serializeJson(doc, json);
        _ws.sendTXT(json);
    }

private:
    WebSocketsClient _ws;
    String _host;
    uint16_t _port;
    String _deviceId;
    String _activationKey;
    bool _secure = false;
    CommandCallback _commandCallback;

    /// serverUrl を解析して host と port を抽出
    /// 例: ws://192.168.1.100:5000 → host=192.168.1.100, port=5000
    /// 例: ws://192.168.1.100 → host=192.168.1.100, port=80
    bool parseServerUrl(const String& serverUrl) {
        // ws:// プロトコルをスキップ
        int schemeEnd = serverUrl.indexOf("://");
        if (schemeEnd < 0) {
            Serial.println("[WS] No scheme in URL");
            return false;
        }

        String scheme = serverUrl.substring(0, schemeEnd);
        _secure = scheme.equalsIgnoreCase("wss");
        if (!_secure && !scheme.equalsIgnoreCase("ws")) {
            Serial.printf("[WS] Unsupported URL scheme: %s\n", scheme.c_str());
            return false;
        }

        String afterScheme = serverUrl.substring(schemeEnd + 3);

        // ポート指定を確認
        int colonPos = afterScheme.indexOf(':');
        int pathPos = afterScheme.indexOf('/');

        if (colonPos >= 0) {
            // ポート指定あり: ws://host:port
            if (pathPos >= 0 && pathPos < colonPos) {
                // /が:の前にある場合は無効
                Serial.println("[WS] Invalid URL format");
                return false;
            }
            _host = afterScheme.substring(0, colonPos);
            String portStr = (pathPos >= 0) ? afterScheme.substring(colonPos + 1, pathPos) : afterScheme.substring(colonPos + 1);
            _port = portStr.toInt();
        } else {
            // ポート指定なし: ws://host (デフォルトポート80)
            _host = (pathPos >= 0) ? afterScheme.substring(0, pathPos) : afterScheme;
            _port = _secure ? 443 : 80;
        }

        if (_host.length() == 0 || _port == 0) {
            Serial.printf("[WS] Invalid host or port: host=%s, port=%d\n", _host.c_str(), _port);
            return false;
        }

        Serial.printf("[WS] Parsed URL: host=%s, port=%d\n", _host.c_str(), _port);
        return true;
    }

    void onEvent(WStype_t type, uint8_t* payload, size_t length) {
        switch (type) {
            case WStype_DISCONNECTED:
                Serial.printf("[WS] Disconnected!\n");
                break;

            case WStype_CONNECTED:
                Serial.printf("[WS] Connected to ws://%s:%d\n", _host.c_str(), _port);
                // 接続成功後に認証情報を送信
                sendAuthMessage();
                break;

            case WStype_TEXT: {
                // JSONメッセージ受信 → コマンド処理
                String message((char*)payload, length);
                Serial.printf("[WS] Received: %s\n", message.c_str());

                JsonDocument doc;
                DeserializationError error = deserializeJson(doc, message);
                if (!error) {
                    String type = doc["type"] | "";
                    if (type == "command" && _commandCallback) {
                        String action = doc["action"] | "";
                        String requestId = doc["requestId"] | "";
                        _commandCallback(action, requestId);
                    }
                }
                break;
            }

            default:
                break;
        }
    }
};
