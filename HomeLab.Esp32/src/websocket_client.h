#pragma once

#include <Arduino.h>
#include <WebSocketsClient.h>
#include <ArduinoJson.h>

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

    void begin(const String& host, uint16_t port, const String& deviceId, const String& activationKey) {
        _host = host;
        _port = port;
        _deviceId = deviceId;
        _activationKey = activationKey;

        String path = "/ws/device?deviceId=" + _deviceId + "&activationKey=" + _activationKey;

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

    bool isConnected() const {
        return _ws.isConnected();
    }

    void onCommand(CommandCallback callback) {
        _commandCallback = callback;
    }

    /// ハートビート送信
    void publishHeartbeat(bool isLocked, int batteryLevel, int wifiRssi) {
        JsonDocument doc;
        doc["type"] = "heartbeat";
        doc["isLocked"] = isLocked;
        doc["batteryLevel"] = batteryLevel;
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
    CommandCallback _commandCallback;

    void onEvent(WStype_t type, uint8_t* payload, size_t length) {
        switch (type) {
            case WStype_DISCONNECTED:
                Serial.printf("[WS] Disconnected!\n");
                break;

            case WStype_CONNECTED:
                Serial.printf("[WS] Connected to ws://%s:%d\n", _host.c_str(), _port);
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
