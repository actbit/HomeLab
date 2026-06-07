/**
 * HomeLab Passkey (WebAuthn) クライアント
 * サーバーAPIと連携してPasskeyの登録・ログインを行う
 */

window.passkeyAuth = {
    /**
     * Passkey登録
     * @param {string} userId - ユーザーID
     * @param {string} apiBase - APIベースURL
     * @param {string} deviceName - デバイス名
     * @returns {Object} 登録結果
     */
    async register(userId, apiBase, deviceName) {
        try {
            // 1. サーバーから登録オプションを取得
            const optionsResponse = await fetch(`${apiBase}/api/auth/passkey/register-options`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ userId })
            });

            if (!optionsResponse.ok) {
                return { success: false, error: '登録オプションの取得に失敗しました' };
            }

            let options = await optionsResponse.json();

            // 2. Base64URL → ArrayBuffer 変換
            options.challenge = base64UrlToArrayBuffer(options.challenge);
            options.user.id = base64UrlToArrayBuffer(options.user.id);
            if (options.excludeCredentials) {
                options.excludeCredentials = options.excludeCredentials.map(c => ({
                    ...c,
                    id: base64UrlToArrayBuffer(c.id)
                }));
            }

            // 3. ブラウザの認証器でPasskey作成
            const credential = await navigator.credentials.create({ publicKey: options });

            // 4. サーバーに登録完了を通知
            const registerResponse = await fetch(`${apiBase}/api/auth/passkey/register`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    id: credential.id,
                    rawId: arrayBufferToBase64Url(credential.rawId),
                    type: credential.type,
                    authenticatorAttachment: credential.authenticatorAttachment || 'cross-platform',
                    responseClientDataJson: arrayBufferToBase64Url(credential.response.clientDataJSON),
                    responseAttestationObject: arrayBufferToBase64Url(credential.response.attestationObject),
                    name: deviceName || navigator.userAgent.includes('Windows') ? 'Windows PC'
                        : navigator.userAgent.includes('Mac') ? 'Mac'
                        : navigator.userAgent.includes('Android') ? 'Android'
                        : navigator.userAgent.includes('iPhone') ? 'iPhone'
                        : 'Unknown Device'
                })
            });

            if (!registerResponse.ok) {
                return { success: false, error: 'Passkey登録に失敗しました' };
            }

            return { success: true };
        } catch (error) {
            console.error('Passkey registration error:', error);
            if (error.name === 'NotAllowedError') {
                return { success: false, error: 'Passkey作成がキャンセルされました' };
            }
            return { success: false, error: error.message };
        }
    },

    /**
     * Passkeyログイン
     * @param {string} apiBase - APIベースURL
     * @param {string} userId - ユーザーID (オプション)
     * @returns {Object} ログイン結果
     */
    async login(apiBase, userId) {
        try {
            // 1. サーバーからログインオプションを取得
            const optionsResponse = await fetch(`${apiBase}/api/auth/passkey/login-options`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ userId: userId || null })
            });

            if (!optionsResponse.ok) {
                return { success: false, error: 'ログインオプションの取得に失敗しました' };
            }

            let options = await optionsResponse.json();

            // 2. Base64URL → ArrayBuffer 変換
            options.challenge = base64UrlToArrayBuffer(options.challenge);
            if (options.allowCredentials) {
                options.allowCredentials = options.allowCredentials.map(c => ({
                    ...c,
                    id: base64UrlToArrayBuffer(c.id)
                }));
            }

            // 3. ブラウザの認証器で認証
            const assertion = await navigator.credentials.get({ publicKey: options });

            // 4. サーバーで検証
            const loginResponse = await fetch(`${apiBase}/api/auth/passkey/login`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    id: assertion.id,
                    rawId: arrayBufferToBase64Url(assertion.rawId),
                    type: assertion.type,
                    responseClientDataJson: arrayBufferToBase64Url(assertion.response.clientDataJSON),
                    responseAuthenticatorData: arrayBufferToBase64Url(assertion.response.authenticatorData),
                    responseSignature: arrayBufferToBase64Url(assertion.response.signature),
                    responseUserHandle: assertion.response.userHandle
                        ? arrayBufferToBase64Url(assertion.response.userHandle)
                        : null
                })
            });

            if (!loginResponse.ok) {
                return { success: false, error: 'Passkey認証に失敗しました' };
            }

            const result = await loginResponse.json();
            return { success: true, token: result.accessToken };
        } catch (error) {
            console.error('Passkey login error:', error);
            if (error.name === 'NotAllowedError') {
                return { success: false, error: '認証がキャンセルされました' };
            }
            return { success: false, error: error.message };
        }
    },

    /**
     * WebAuthn API がサポートされているか確認
     */
    isSupported() {
        return typeof navigator !== 'undefined'
            && !!window.PublicKeyCredential;
    }
};

// ===== Base64URL ユーティリティ =====

function base64UrlToArrayBuffer(base64Url) {
    // Base64URL → Base64
    let base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    // パディング追加
    while (base64.length % 4) {
        base64 += '=';
    }
    const binaryString = atob(base64);
    const bytes = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
    }
    return bytes.buffer;
}

function arrayBufferToBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.byteLength; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    // Base64 → Base64URL
    return btoa(binary)
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=/g, '');
}
