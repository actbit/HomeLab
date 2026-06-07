/**
 * HomeLab ESP32 BLE Provisioning
 * Web Bluetooth API を使用してESP32とBLE通信し、
 * WiFi設定・SESAME情報・アクティベーションキーを書き込む
 */

// ESP32 BLE サービス・キャラクタリスティック UUID
const HOMELAB_SERVICE_UUID = "0000fe40-cc7a-482a-984a-df91a4f35e6a";

const CHARACTERISTICS = {
    DEVICE_ID:         "0000fe41-8e22-4541-9d4c-21edae82e5a1",  // Read
    DEVICE_PUBLIC_KEY: "0000fe42-8e22-4541-9d4c-21edae82e5a1",  // Read
    WIFI_SSID:         "0000fe43-8e22-4541-9d4c-21edae82e5a1",  // Write
    WIFI_PASSWORD:     "0000fe44-8e22-4541-9d4c-21edae82e5a1",  // Write
    SESAME_UUID:       "0000fe45-8e22-4541-9d4c-21edae82e5a1",  // Write
    SESAME_API_KEY:    "0000fe46-8e22-4541-9d4c-21edae82e5a1",  // Write
    ACTIVATION_KEY:    "0000fe47-8e22-4541-9d4c-21edae82e5a1",  // Write
    MQTT_BROKER_URL:   "0000fe48-8e22-4541-9d4c-21edae82e5a1",  // Write
    STATUS:            "0000fe49-8e22-4541-9d4c-21edae82e5a1",  // Notify
};

let connectedDevice = null;
let statusCharacteristic = null;

/**
 * ESP32にBLE接続
 * @returns {Object} デバイスIDと公開鍵
 */
window.bleProvisioning = {
    /**
     * ESP32デバイスに接続し、デバイス情報を取得
     */
    async connect() {
        try {
            connectedDevice = await navigator.bluetooth.requestDevice({
                filters: [{ services: [HOMELAB_SERVICE_UUID] }],
                optionalServices: [HOMELAB_SERVICE_UUID]
            });

            const server = await connectedDevice.gatt.connect();
            const service = await server.getPrimaryService(HOMELAB_SERVICE_UUID);

            // デバイスID読み取り
            const deviceIdChar = await service.getCharacteristic(CHARACTERISTICS.DEVICE_ID);
            const deviceId = await deviceIdChar.readValue();

            // 公開鍵読み取り
            const publicKeyChar = await service.getCharacteristic(CHARACTERISTICS.DEVICE_PUBLIC_KEY);
            const publicKey = await publicKeyChar.readValue();

            // ステータス通知を購読
            statusCharacteristic = await service.getCharacteristic(CHARACTERISTICS.STATUS);
            await statusCharacteristic.startNotifications();
            statusCharacteristic.addEventListener(
                "characteristicvaluechanged",
                handleStatusNotification
            );

            return {
                success: true,
                deviceId: new TextDecoder().decode(deviceId),
                publicKey: arrayBufferToBase64(publicKey.buffer),
            };
        } catch (error) {
            console.error("BLE connection failed:", error);
            return { success: false, error: error.message };
        }
    },

    /**
     * WiFi設定をESP32に書き込み
     */
    async writeWifiCredentials(ssid, password) {
        try {
            const service = await getService();
            const ssidChar = await service.getCharacteristic(CHARACTERISTICS.WIFI_SSID);
            await ssidChar.writeValue(new TextEncoder().encode(ssid));

            const passwordChar = await service.getCharacteristic(CHARACTERISTICS.WIFI_PASSWORD);
            await passwordChar.writeValue(new TextEncoder().encode(password));

            return { success: true };
        } catch (error) {
            console.error("WiFi write failed:", error);
            return { success: false, error: error.message };
        }
    },

    /**
     * SESAME デバイス情報をESP32に書き込み
     */
    async writeSesameInfo(sesameUuid, sesameApiKey) {
        try {
            const service = await getService();
            const uuidChar = await service.getCharacteristic(CHARACTERISTICS.SESAME_UUID);
            await uuidChar.writeValue(new TextEncoder().encode(sesameUuid));

            const keyChar = await service.getCharacteristic(CHARACTERISTICS.SESAME_API_KEY);
            await keyChar.writeValue(new TextEncoder().encode(sesameApiKey));

            return { success: true };
        } catch (error) {
            console.error("SESAME info write failed:", error);
            return { success: false, error: error.message };
        }
    },

    /**
     * アクティベーションキーをESP32に書き込み
     */
    async writeActivationKey(activationKey) {
        try {
            const service = await getService();
            const keyChar = await service.getCharacteristic(CHARACTERISTICS.ACTIVATION_KEY);
            await keyChar.writeValue(new TextEncoder().encode(activationKey));

            return { success: true };
        } catch (error) {
            console.error("Activation key write failed:", error);
            return { success: false, error: error.message };
        }
    },

    /**
     * MQTTブローカーURLをESP32に書き込み
     */
    async writeMqttBrokerUrl(url) {
        try {
            const service = await getService();
            const urlChar = await service.getCharacteristic(CHARACTERISTICS.MQTT_BROKER_URL);
            await urlChar.writeValue(new TextEncoder().encode(url));

            return { success: true };
        } catch (error) {
            console.error("MQTT URL write failed:", error);
            return { success: false, error: error.message };
        }
    },

    /**
     * BLE接続を切断
     */
    async disconnect() {
        if (connectedDevice && connectedDevice.gatt.connected) {
            connectedDevice.gatt.disconnect();
        }
        connectedDevice = null;
        statusCharacteristic = null;
        return { success: true };
    },

    /**
     * Web Bluetooth API がサポートされているか確認
     */
    isSupported() {
        return typeof navigator !== "undefined" && !!navigator.bluetooth;
    }
};

/**
 * GATTサービスを取得
 */
async function getService() {
    if (!connectedDevice || !connectedDevice.gatt.connected) {
        throw new Error("Device not connected");
    }
    const server = await connectedDevice.gatt.connect();
    return await server.getPrimaryService(HOMELAB_SERVICE_UUID);
}

/**
 * ArrayBuffer を Base64 文字列に変換
 */
function arrayBufferToBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = "";
    for (let i = 0; i < bytes.byteLength; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

/**
 * ESP32からのステータス通知を処理
 */
function handleStatusNotification(event) {
    const value = new TextDecoder().decode(event.target.value);
    console.log("ESP32 Status:", value);

    // Blazorに通知 (DotNet.invokeMethod で)
    if (window.DotNet) {
        DotNet.invokeMethodAsync("HomeLab.Client", "OnBleStatusReceived", value);
    }
}
