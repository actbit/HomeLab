#include "crypto.h"

#include <Arduino.h>
#include <WiFi.h>
#include "mbedtls/ecdsa.h"
#include "mbedtls/entropy.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/sha256.h"
#include "mbedtls/base64.h"

/**
 * ESP32 内蔵の mbedTLS を使用した ECDSA P-256 鍵ペア生成・署名
 * (micro-ecc の代わりに mbedTLS を使用 - ESP32 に標準搭載)
 */

namespace Crypto {

// mbedTLS コンテキスト (一度初期化して使い回す)
static mbedtls_entropy_context _entropy;
static mbedtls_ctr_drbg_context _ctr_drbg;
static bool _initialized = false;

/**
 * mbedTLS RNG 初期化
 */
static bool ensureInitialized() {
    if (_initialized) return true;

    mbedtls_entropy_init(&_entropy);
    mbedtls_ctr_drbg_init(&_ctr_drbg);

    // エントロピーソースを追加 (ESP32のハードウェアRNG使用)
    int ret = mbedtls_ctr_drbg_seed(&_ctr_drbg,
                                    mbedtls_entropy_func,
                                    &_entropy,
                                    nullptr, 0);
    if (ret != 0) {
        Serial.printf("[CRYPTO] CTR_DRBG seed failed: -0x%04x\n", -ret);
        return false;
    }

    _initialized = true;
    return true;
}

/**
 * ECDSA P-256 鍵ペア生成
 */
bool generateKeyPair(uint8_t* publicKey, uint8_t* privateKey) {
    if (!ensureInitialized()) return false;

    mbedtls_ecdsa_context ctx;
    mbedtls_ecdsa_init(&ctx);

    // SECP256R1 (P-256) キーペア生成
    int ret = mbedtls_ecdsa_genkey(&ctx, MBEDTLS_ECP_DP_SECP256R1,
                                   mbedtls_ctr_drbg_random, &_ctr_drbg);
    if (ret != 0) {
        Serial.printf("[CRYPTO] Key generation failed: -0x%04x\n", -ret);
        mbedtls_ecdsa_free(&ctx);
        return false;
    }

    // 秘密鍵をエクスポート (32 bytes)
    ret = mbedtls_mpi_write_binary(&ctx.d, privateKey, 32);
    if (ret != 0) {
        Serial.printf("[CRYPTO] Private key export failed: -0x%04x\n", -ret);
        mbedtls_ecdsa_free(&ctx);
        return false;
    }

    // 公開鍵をエクスポート (非圧縮形式からX,Y座標を抽出 = 64 bytes)
    size_t pubLen = 0;
    uint8_t pubRaw[65]; // 非圧縮形式 = 0x04 + X(32) + Y(32)
    ret = mbedtls_ecp_point_write_binary(&ctx.grp, &ctx.Q,
                                         MBEDTLS_ECP_PF_UNCOMPRESSED,
                                         &pubLen, pubRaw, sizeof(pubRaw));
    if (ret != 0 || pubLen != 65) {
        Serial.printf("[CRYPTO] Public key export failed: -0x%04x, len=%d\n", -ret, pubLen);
        mbedtls_ecdsa_free(&ctx);
        return false;
    }

    // X座標(32) + Y座標(32) をコピー (0x04プレフィックスをスキップ)
    memcpy(publicKey, pubRaw + 1, 64);

    mbedtls_ecdsa_free(&ctx);
    Serial.println("[CRYPTO] ECDSA P-256 key pair generated");
    return true;
}

/**
 * ECDSA P-256 署名
 */
bool sign(const uint8_t* message, size_t messageLen,
          const uint8_t* privateKey, uint8_t* signature) {
    if (!ensureInitialized()) return false;

    // SHA-256 ハッシュ
    uint8_t hash[32];
    mbedtls_sha256(message, messageLen, hash, 0); // 0 = SHA-256

    // ECDSA コンテキスト設定
    mbedtls_ecdsa_context ctx;
    mbedtls_ecdsa_init(&ctx);

    int ret = mbedtls_ecdsa_from_keypair(&ctx,
                                          MBEDTLS_ECP_DP_SECP256R1,
                                          nullptr, // Q (署名に不要)
                                          privateKey);
    if (ret != 0) {
        // グループ設定 + 秘密鍵読み込み
        ret = mbedtls_ecp_group_load(&ctx.grp, MBEDTLS_ECP_DP_SECP256R1);
        if (ret != 0) {
            Serial.printf("[CRYPTO] Group load failed: -0x%04x\n", -ret);
            mbedtls_ecdsa_free(&ctx);
            return false;
        }
        ret = mbedtls_mpi_read_binary(&ctx.d, privateKey, 32);
        if (ret != 0) {
            Serial.printf("[CRYPTO] Private key load failed: -0x%04x\n", -ret);
            mbedtls_ecdsa_free(&ctx);
            return false;
        }
    }

    // 署名生成
    mbedtls_mpi r, s;
    mbedtls_mpi_init(&r);
    mbedtls_mpi_init(&s);

    ret = mbedtls_ecdsa_sign(&ctx.grp, &r, &s, &ctx.d,
                              hash, 32,
                              mbedtls_ctr_drbg_random, &_ctr_drbg);
    if (ret != 0) {
        Serial.printf("[CRYPTO] Signing failed: -0x%04x\n", -ret);
        mbedtls_mpi_free(&r);
        mbedtls_mpi_free(&s);
        mbedtls_ecdsa_free(&ctx);
        return false;
    }

    // r(32 bytes) + s(32 bytes) を出力
    ret = mbedtls_mpi_write_binary(&r, signature, 32);
    if (ret == 0) {
        ret = mbedtls_mpi_write_binary(&s, signature + 32, 32);
    }

    mbedtls_mpi_free(&r);
    mbedtls_mpi_free(&s);
    mbedtls_ecdsa_free(&ctx);

    if (ret != 0) {
        Serial.printf("[CRYPTO] Signature export failed: -0x%04x\n", -ret);
        return false;
    }

    return true;
}

/**
 * デバイスID生成 (MACアドレス + ランダム)
 */
String generateDeviceId() {
    uint8_t mac[6];
    esp_read_mac(mac, ESP_MAC_WIFI_STA);

    uint8_t rnd[4];
    esp_fill_random(rnd, 4);

    char id[21]; // "hl-" + 12 hex (mac) + "-" + 8 hex (rnd) + null
    snprintf(id, sizeof(id), "hl-%02x%02x%02x%02x%02x%02x-%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5],
             rnd[0], rnd[1], rnd[2], rnd[3]);

    return String(id);
}

/**
 * 公開鍵をBase64エンコード
 */
String publicKeyToBase64(const uint8_t* publicKey) {
    // 非圧縮形式にプレフィックス追加 (0x04)
    uint8_t uncompressed[65];
    uncompressed[0] = 0x04;
    memcpy(uncompressed + 1, publicKey, 64);

    size_t outLen = 0;
    mbedtls_base64_encode(nullptr, 0, &outLen, uncompressed, 65);

    String result;
    result.reserve(outLen);
    result.resize(outLen - 1); // null終端分除外

    mbedtls_base64_encode((unsigned char*)result.begin(),
                          outLen, &outLen, uncompressed, 65);

    return result;
}

} // namespace Crypto
