#pragma once

#include <Arduino.h>
#include <PubSubClient.h>
#include <WiFiClient.h>
#include "config.h"

/**
 * MQTTクライアント
 * サーバーとの通信、コマンド受信、ステータス/ハートビート送信
 */
class MqttClient {
public:
    using CommandCallback = std::function<void(const String& action, const String& requestId)>;

    /**
     * 初期化
     */
    void begin(const String& brokerUrl, int port, const String& deviceId) {
        _deviceId = deviceId;
        _brokerUrl = brokerUrl;
        _port = port;

        _wifiClient = std::make_unique<WiFiClient>();
        _mqtt.setClient(*_wifiClient);
        _mqtt.setServer(brokerUrl.c_str(), port);
        _mqtt.setCallback([this](char* topic, uint8_t* payload, unsigned int length) {
            this->onMessage(topic, payload, length);
        });
        _mqtt.setKeepAlive(60);
    }

    /**
     * MQTT接続 (自動リトライ付き)
     */
    bool connect(const String& tenantId) {
        _tenantId = tenantId;

        String clientId = "homelab-" + _deviceId;
        String statusTopic = "homelab/" + _tenantId + "/" + _deviceId + "/status";
        String commandTopic = "homelab/" + _tenantId + "/" + _deviceId + "/command";

        if (_mqtt.connect(clientId.c_str())) {
            Serial.printf("[MQTT] Connected to %s:%d\n", _brokerUrl.c_str(), _port);
            _mqtt.subscribe(commandTopic.c_str());
            Serial.printf("[MQTT] Subscribed to %s\n", commandTopic.c_str());
            return true;
        }

        Serial.printf("[MQTT] Connection failed, rc=%d\n", _mqtt.state());
        return false;
    }

    /**
     * ループ処理 (メインループで呼び出し)
     */
    void loop() {
        if (!_mqtt.connected()) {
            reconnect();
        }
        _mqtt.loop();
    }

    /**
     * ステータスメッセージ送信
     */
    bool publishStatus(const String& state, const String& requestId,
                       bool success = true, const String& error = "") {
        String topic = "homelab/" + _tenantId + "/" + _deviceId + "/status";

        String payload = "{\"state\":\"" + state + "\""
                       + ",\"requestId\":\"" + requestId + "\""
                       + ",\"timestamp\":\"" + getTimestamp() + "\""
                       + ",\"success\":" + (success ? "true" : "false");

        if (error.length() > 0) {
            payload += ",\"errorMessage\":\"" + error + "\"";
        }
        payload += "}";

        return _mqtt.publish(topic.c_str(), payload.c_str());
    }

    /**
     * ハートビート送信
     */
    bool publishHeartbeat(bool isLocked, int batteryLevel, int wifiRssi) {
        String topic = "homelab/" + _tenantId + "/" + _deviceId + "/heartbeat";

        String payload = "{\"deviceId\":\"" + _deviceId + "\""
                       + ",\"isLocked\":" + (isLocked ? "true" : "false")
                       + ",\"batteryLevel\":" + String(batteryLevel)
                       + ",\"wifiRssi\":" + String(wifiRssi)
                       + ",\"timestamp\":\"" + getTimestamp() + "\""
                       + "}";

        return _mqtt.publish(topic.c_str(), payload.c_str());
    }

    /**
     * コマンドコールバック設定
     */
    void onCommand(CommandCallback callback) {
        _commandCallback = callback;
    }

    bool isConnected() const { return _mqtt.connected(); }

private:
    std::unique_ptr<WiFiClient> _wifiClient;
    PubSubClient _mqtt;
    String _deviceId;
    String _tenantId;
    String _brokerUrl;
    int _port;
    CommandCallback _commandCallback;
    unsigned long _lastReconnectAttempt = 0;

    void onMessage(char* topic, uint8_t* payload, unsigned int length) {
        String message;
        for (unsigned int i = 0; i < length; i++) {
            message += (char)payload[i];
        }

        Serial.printf("[MQTT] Received on %s: %s\n", topic, message.c_str());

        // JSONパース (ArduinoJson使用推奨だが簡略化)
        // actionとrequestIdを抽出
        String action = extractJsonField(message, "action");
        String requestId = extractJsonField(message, "requestId");

        if (_commandCallback && action.length() > 0) {
            _commandCallback(action, requestId);
        }
    }

    void reconnect() {
        unsigned long now = millis();
        if (now - _lastReconnectAttempt < 5000) return;
        _lastReconnectAttempt = now;

        Serial.println("[MQTT] Attempting reconnect...");
        connect(_tenantId);
    }

    String extractJsonField(const String& json, const String& field) {
        String searchKey = "\"" + field + "\":\"";
        int startIdx = json.indexOf(searchKey);
        if (startIdx < 0) return "";
        startIdx += searchKey.length();
        int endIdx = json.indexOf("\"", startIdx);
        if (endIdx < 0) return "";
        return json.substring(startIdx, endIdx);
    }

    String getTimestamp() {
        return String(millis());
    }
};
