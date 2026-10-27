// Requires Playwright and a disposable server with Passkey:Origin matching the URL.
const { chromium } = require('playwright');

async function virtualAuthenticator(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
    protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
    hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true
  } });
}

async function main() {
  const base = process.env.HOME_LAB_TEST_URL || 'http://localhost:5234';
  const browser = await chromium.launch({ headless: true, ...(process.env.HOME_LAB_BROWSER ? { executablePath: process.env.HOME_LAB_BROWSER } : {}) });
  try {
    const owner = await browser.newPage();
    owner.on('pageerror', error => console.error('Owner browser error:', error));
    owner.on('dialog', dialog => dialog.accept());
    await owner.goto(`${base}/register`);
    await owner.getByLabel('表示名').fill('Owner');
    const email = `temporary-${Date.now()}@example.com`;
    await owner.getByLabel('メールアドレス').fill(email);
    await owner.locator('input[autocomplete="new-password"]').nth(0).fill('TestPassword123!');
    await owner.locator('input[autocomplete="new-password"]').nth(1).fill('TestPassword123!');
    await owner.getByRole('button', { name: 'アカウント作成' }).click();
    await owner.waitForURL('**/login');
    await owner.getByLabel('メールアドレス').fill(email);
    await owner.locator('input[autocomplete="current-password"]').fill('TestPassword123!');
    await owner.getByRole('button', { name: 'ログイン', exact: true }).click();
    await owner.waitForURL(`${base}/`);
    const registered = await owner.evaluate(async () => {
      const identifier = `temporary-${Date.now()}`;
      const response = await fetch('/api/devices/register', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ devicePublicKey: 'test', deviceId: identifier, sesameDeviceUuid: 'test', name: 'Guest Lock' }) });
      if (!response.ok) throw new Error(`device registration ${response.status}`);
      return { ...(await response.json()), identifier };
    });
    const device = await browser.newPage();
    await device.goto(`${base}/login`);
    await device.evaluate(async ({ identifier, activationKey }) => {
      const socket = new WebSocket(`ws://${location.host}/ws/device`);
      window.testSocket = socket;
      window.commands = [];
      await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
      socket.onmessage = event => {
        const command = JSON.parse(event.data);
        window.commands.push(command);
        socket.send(JSON.stringify({ type: 'status', state: 'unlocked', stateConfirmed: true, requestId: command.requestId, success: true }));
      };
      socket.send(JSON.stringify({ deviceId: identifier, activationKey }));
      socket.send(JSON.stringify({ type: 'heartbeat', stateConfirmed: true, isLocked: true }));
    }, registered);
    await owner.getByText('Guest Lock').waitFor();
    await owner.getByText('Guest Lock').click();
    await owner.getByLabel('キーの名前').fill('訪問者');
    await owner.getByLabel('解錠できる回数').fill('1');
    await owner.getByRole('button', { name: '一時キーを作成' }).click();
    const link = await owner.getByLabel('共有リンク（作成直後のみ表示）').inputValue();
    if (!link.includes('#')) throw new Error('Missing secret fragment');

    const guest = await browser.newPage();
    guest.on('pageerror', error => console.error('Guest browser error:', error));
    await virtualAuthenticator(guest);
    await guest.goto(link);
    await guest.getByRole('button', { name: 'パスキーを登録' }).click();
    await guest.getByText('パスキーを登録しました。この端末で一時キーを利用できます。').waitFor();
    await guest.getByRole('button', { name: 'パスキーで解錠' }).click();
    await guest.getByText('残り 0 回').waitFor();
    await guest.getByText('前回の操作: 成功').waitFor();
    if (await guest.getByRole('button', { name: 'パスキーで解錠' }).isEnabled()) throw new Error('Exhausted key still allows unlocking');
    const commands = await device.evaluate(() => window.commands);
    if (commands.length !== 1 || commands[0].action !== 'unlock') throw new Error('Incorrect number of unlock commands');
    await owner.getByRole('button', { name: '無効化' }).click({ force: true });
    await guest.getByText('一時キーが無効か期限切れです。').waitFor();

    await owner.getByLabel('キーの名前').fill('同時操作');
    await owner.getByRole('button', { name: '一時キーを作成' }).click();
    await owner.waitForFunction(previous => document.querySelector('input[readonly]')?.value !== previous, link);
    const secondLink = await owner.getByLabel('共有リンク（作成直後のみ表示）').inputValue();
    const token = secondLink.split('#')[1];
    const secondGuest = await browser.newPage();
    await virtualAuthenticator(secondGuest);
    await secondGuest.goto(secondLink);
    await secondGuest.getByRole('button', { name: 'パスキーを登録' }).click();
    await secondGuest.getByText('パスキーを登録しました。この端末で一時キーを利用できます。').waitFor();
    const attempt = await secondGuest.evaluate(async token => {
      const post = async (path, body) => fetch(`/api/temporary-keys/${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const assertion = async () => {
        const start = await (await post('unlock-options', { token })).json();
        start.options.challenge = passkeyDecode(start.options.challenge);
        start.options.allowCredentials = start.options.allowCredentials.map(c => ({ ...c, id: passkeyDecode(c.id) }));
        const c = await navigator.credentials.get({ publicKey: start.options });
        return { token, challengeId: start.challengeId, credential: { id: c.id, rawId: passkeyEncode(c.rawId), type: c.type,
          response: { clientDataJSON: passkeyEncode(c.response.clientDataJSON), authenticatorData: passkeyEncode(c.response.authenticatorData),
            signature: passkeyEncode(c.response.signature), userHandle: c.response.userHandle ? passkeyEncode(c.response.userHandle) : null },
          clientExtensionResults: c.getClientExtensionResults() } };
      };
      const invalid = await assertion();
      invalid.credential.response.signature = passkeyEncode(new Uint8Array(64));
      const rejected = (await post('unlock', invalid)).status;
      const status = await (await post('status', { token })).json();
      const first = await assertion();
      const second = await assertion();
      const responses = await Promise.all([post('unlock', first), post('unlock', second)]);
      const replay = (await post('unlock', first)).status;
      return { rejected, remainingAfterInvalid: status.remainingUses, codes: responses.map(r => r.status), replay };
    }, token);
    if (attempt.rejected !== 400 || attempt.remainingAfterInvalid !== 1 || attempt.codes.filter(code => code === 200).length !== 1 ||
        attempt.codes.some(code => ![200, 404, 409].includes(code)) || ![400, 404, 409].includes(attempt.replay))
      throw new Error(`Invalid signature, quota, or replay check failed: ${JSON.stringify(attempt)}`);
    await device.waitForFunction(() => window.commands.length === 2);
    console.log('Temporary key passkey registration, unlock, revocation, invalid signature, replay and concurrent quota succeeded');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
