#pragma once

#include <Arduino.h>
#include "config.h"

/**
 * ECDSA P-256 署名ユーティリティ
 * micro-ecc を使用してデバイス認証用の鍵ペア生成・署名を行う
 */
namespace Crypto {

    /**
     * ECDSA鍵ペアを生成
     * @param publicKey 出力先 (64 bytes = 2 * 32 byte座標)
     * @param privateKey 出力先 (32 bytes)
     * @return 成功/失敗
     */
    bool generateKeyPair(uint8_t* publicKey, uint8_t* privateKey);

    /**
     * メッセージにECDSA署名
     * @param message メッセージデータ
     * @param messageLen メッセージ長
     * @param privateKey 秘密鍵 (32 bytes)
     * @param signature 出力先 (64 bytes = r + s)
     * @return 成功/失敗
     */
    bool sign(const uint8_t* message, size_t messageLen,
              const uint8_t* privateKey, uint8_t* signature);

    /**
     * デバイスIDを生成 (ESP32 MACアドレス + ランダム要素)
     */
    String generateDeviceId();

    /**
     * 公開鍵をBase64エンコード
     */
    String publicKeyToBase64(const uint8_t* publicKey);

}
