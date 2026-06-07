#pragma once

#include <Arduino.h>
#include "config.h"

/**
 * ECDSA P-256 署名ユーティリティ
 * micro-ecc を使用
 */
namespace Crypto {

    /**
     * ECDSA鍵ペアを生成
     * @param publicKey 出力先 (64 bytes)
     * @param privateKey 出力先 (32 bytes)
     * @return 成功/失敗
     */
    bool generateKeyPair(uint8_t* publicKey, uint8_t* privateKey);

    /**
     * メッセージにECDSA署名
     * @param message メッセージデータ
     * @param messageLen メッセージ長
     * @param privateKey 秘密鍵 (32 bytes)
     * @param signature 出力先 (64 bytes)
     * @return 成功/失敗
     */
    bool sign(const uint8_t* message, size_t messageLen,
              const uint8_t* privateKey, uint8_t* signature);

    /**
     * デバイスIDを生成 (ESP32のMACアドレスベース)
     */
    String generateDeviceId();

    /**
     * 公開鍵をBase64エンコード
     */
    String publicKeyToBase64(const uint8_t* publicKey);

}
