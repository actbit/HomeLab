// WebAuthn binary values are represented as base64url in the FIDO2 API.
function passkeyDecode(value) {
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const binary = atob(base64 + '='.repeat((4 - base64.length % 4) % 4));
    return Uint8Array.from(binary, char => char.charCodeAt(0));
}

function passkeyEncode(value) {
    return btoa(String.fromCharCode(...new Uint8Array(value)))
        .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

window.passkeyAuth = {
    isSupported() { return !!window.PublicKeyCredential && window.isSecureContext; },
    async temporary(token, action) {
        const post = async (path, body) => {
            const response = await fetch(`/api/temporary-keys/${path}`, {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
            });
            if (!response.ok) {
                const body = await response.json().catch(() => ({}));
                throw new Error(body.error || '一時キーを利用できませんでした');
            }
            return response.status === 204 ? null : response.json().catch(() => ({}));
        };
        const { challengeId, options } = await post(`${action}-options`, { token });
        options.challenge = passkeyDecode(options.challenge);
        let credential;
        let response;
        if (action === 'register') {
            options.user.id = passkeyDecode(options.user.id);
            options.excludeCredentials = (options.excludeCredentials || []).map(c => ({ ...c, id: passkeyDecode(c.id) }));
            credential = await navigator.credentials.create({ publicKey: options });
            if (!credential) throw new Error('パスキー登録がキャンセルされました');
            response = { clientDataJSON: passkeyEncode(credential.response.clientDataJSON),
                attestationObject: passkeyEncode(credential.response.attestationObject),
                transports: credential.response.getTransports ? credential.response.getTransports() : [] };
        } else {
            options.allowCredentials = (options.allowCredentials || []).map(c => ({ ...c, id: passkeyDecode(c.id) }));
            credential = await navigator.credentials.get({ publicKey: options });
            if (!credential) throw new Error('パスキー認証がキャンセルされました');
            response = { clientDataJSON: passkeyEncode(credential.response.clientDataJSON),
                authenticatorData: passkeyEncode(credential.response.authenticatorData),
                signature: passkeyEncode(credential.response.signature),
                userHandle: credential.response.userHandle ? passkeyEncode(credential.response.userHandle) : null };
        }
        return post(action, { token, challengeId, credential: {
            id: credential.id, rawId: passkeyEncode(credential.rawId), type: credential.type,
            response, clientExtensionResults: credential.getClientExtensionResults()
        } });
    },
    async register(name) {
        try {
            const start = await fetch('/api/auth/passkey/register-options', { method: 'POST', credentials: 'same-origin' });
            if (!start.ok) throw new Error('登録を開始できませんでした');
            const { challengeId, options } = await start.json();
            options.challenge = passkeyDecode(options.challenge);
            options.user.id = passkeyDecode(options.user.id);
            options.excludeCredentials = (options.excludeCredentials || []).map(c => ({ ...c, id: passkeyDecode(c.id) }));
            const credential = await navigator.credentials.create({ publicKey: options });
            if (!credential) throw new Error('パスキー登録がキャンセルされました');
            const result = await fetch('/api/auth/passkey/register', {
                method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ challengeId, name, credential: {
                    id: credential.id, rawId: passkeyEncode(credential.rawId), type: credential.type,
                    response: { clientDataJSON: passkeyEncode(credential.response.clientDataJSON),
                        attestationObject: passkeyEncode(credential.response.attestationObject),
                        transports: credential.response.getTransports ? credential.response.getTransports() : [] },
                    clientExtensionResults: credential.getClientExtensionResults()
                } })
            });
            if (!result.ok) throw new Error('パスキーを検証できませんでした');
            return { success: true };
        } catch (error) { return { success: false, error: error.message || 'パスキー登録に失敗しました' }; }
    },
    async login() {
        try {
            const start = await fetch('/api/auth/passkey/login-options', { method: 'POST', credentials: 'same-origin' });
            if (!start.ok) throw new Error('認証を開始できませんでした');
            const { challengeId, options } = await start.json();
            options.challenge = passkeyDecode(options.challenge);
            options.allowCredentials = (options.allowCredentials || []).map(c => ({ ...c, id: passkeyDecode(c.id) }));
            const credential = await navigator.credentials.get({ publicKey: options });
            if (!credential) throw new Error('パスキー認証がキャンセルされました');
            const result = await fetch('/api/auth/passkey/login', {
                method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ challengeId, credential: {
                    id: credential.id, rawId: passkeyEncode(credential.rawId), type: credential.type,
                    response: { clientDataJSON: passkeyEncode(credential.response.clientDataJSON),
                        authenticatorData: passkeyEncode(credential.response.authenticatorData),
                        signature: passkeyEncode(credential.response.signature),
                        userHandle: credential.response.userHandle ? passkeyEncode(credential.response.userHandle) : null },
                    clientExtensionResults: credential.getClientExtensionResults()
                } })
            });
            if (!result.ok) throw new Error('パスキーを検証できませんでした');
            return { success: true };
        } catch (error) { return { success: false, error: error.message || 'パスキー認証に失敗しました' }; }
    }
};
