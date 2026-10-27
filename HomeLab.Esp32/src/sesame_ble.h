#pragma once

#include <Arduino.h>
#include <NimBLEDevice.h>
#include <Sesame.h>
#include <SesameClient.h>
#include <string_view>

/**
 * SESAMEスマートロックのBLE制御。
 * libsesame3bt 0.33以降の非同期APIに合わせ、状態とバッテリーは
 * status callbackから更新する。
 */
class SesameBle {
public:
    void begin(const String& deviceAddress, const String& secretKey,
               libsesame3bt::Sesame::model_t model = libsesame3bt::Sesame::model_t::sesame_5,
               uint8_t addressType = BLE_ADDR_RANDOM) {
        _secretKey = secretKey;
        _model = model;
        _address = NimBLEAddress(deviceAddress.c_str(), addressType);

        _initialized = _client.begin(_address, _model);
        if (!_initialized) {
            Serial.printf("[SESAME] Failed to initialize %s\n", deviceAddress.c_str());
            return;
        }

        if (!_client.set_keys("", _secretKey.c_str())) {
            _initialized = false;
            Serial.println("[SESAME] Invalid secret key");
            return;
        }
        _client.set_connect_timeout(5000);
        _client.set_status_callback([](libsesame3bt::SesameClient&, libsesame3bt::SesameClient::Status status) {
            _isLocked = status.in_lock();
            _hasStatus = status.in_lock() != status.in_unlock() && !status.is_critical() && !status.is_clutch_failed();
            ++_statusRevision;
            _batteryLevel = constrain(static_cast<int>(status.battery_pct()), 0, 100);
        });

        Serial.printf("[SESAME] Initialized for %s (model=%d)\n",
                      deviceAddress.c_str(), static_cast<int>(model));
    }

    bool connect() {
        if (!_initialized) return false;
        if (_client.is_session_active()) {
            _connected = true;
            return true;
        }

        Serial.println("[SESAME] Connecting...");
        _hasStatus = false;
        if (!_client.connect(0)) {
            _connected = false;
            Serial.println("[SESAME] Connection failed");
            return false;
        }

        _connected = _client.is_session_active();
        if (_connected) {
            _client.request_status();
            Serial.println("[SESAME] Connected");
        }
        return _connected;
    }

    bool ensureConnected() {
        if (_connected && _client.is_session_active()) return true;
        _connected = false;

        _hasStatus = false;
        if (_hasAttempted && millis() - _lastConnectAttempt < RECONNECT_INTERVAL_MS) return false;
        _hasAttempted = true;
        _lastConnectAttempt = millis();
        return connect();
    }

    bool unlock(const char* tag = "HomeLock") {
        return ensureConnected() && _client.unlock(std::string_view(tag));
    }

    bool lock(const char* tag = "HomeLock") {
        return ensureConnected() && _client.lock(std::string_view(tag));
    }

    bool toggle(const char* tag = "HomeLock") {
        return _hasStatus && (_isLocked ? unlock(tag) : lock(tag));
    }

    void disconnect() {
        if (_initialized) _client.disconnect();
        _connected = false;
        _hasStatus = false;
    }

    // libsesame3btはBLEイベントをNimBLEが処理するため、互換用に空実装を残す。
    void loop() {}

    bool isConnected() const { return _connected && _client.is_session_active(); }
    bool isInitialized() const { return _initialized; }
    bool isLocked() const { return _isLocked; }
    bool hasStatus() const { return isConnected() && _hasStatus; }
    uint32_t statusRevision() const { return _statusRevision; }
    int getBatteryLevel() const { return _batteryLevel; }

    void updateStatus() {
        if (isConnected()) _client.request_status();
    }

private:
    libsesame3bt::SesameClient _client;
    NimBLEAddress _address;
    String _secretKey;
    libsesame3bt::Sesame::model_t _model = libsesame3bt::Sesame::model_t::sesame_5;
    bool _initialized = false;
    bool _connected = false;
    inline static volatile bool _isLocked = true;
    inline static volatile bool _hasStatus = false;
    inline static volatile uint32_t _statusRevision = 0;
    inline static volatile int _batteryLevel = -1;
    unsigned long _lastConnectAttempt = 0;
    bool _hasAttempted = false;
    static constexpr uint32_t RECONNECT_INTERVAL_MS = 30000;
};
