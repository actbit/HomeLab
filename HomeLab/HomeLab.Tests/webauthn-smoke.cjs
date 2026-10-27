// Optional browser integration smoke test; install Playwright and point it at a
// disposable development server whose Passkey origin matches HOME_LAB_TEST_URL.
const { chromium } = require('playwright');

async function main() {
  const baseUrl = process.env.HOME_LAB_TEST_URL || 'http://localhost:5234';
  const browser = await chromium.launch({
    headless: true,
    ...(process.env.HOME_LAB_BROWSER ? { executablePath: process.env.HOME_LAB_BROWSER } : {})
  });
  try {
    const page = await browser.newPage();
    page.on('pageerror', error => console.error('Browser error:', error.message));
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
      hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true
    } });
    const email = `webauthn-${Date.now()}@example.com`;
    await page.goto(`${baseUrl}/register`);
    await page.getByLabel('表示名').fill('WebAuthn smoke');
    await page.getByLabel('メールアドレス').fill(email);
    await page.locator('input[autocomplete="new-password"]').nth(0).fill('TestPassword123!');
    await page.locator('input[autocomplete="new-password"]').nth(1).fill('TestPassword123!');
    await page.getByRole('button', { name: 'アカウント作成' }).click();
    await page.waitForURL('**/login');
    await page.getByLabel('メールアドレス').fill(email);
    await page.locator('input[autocomplete="current-password"]').fill('TestPassword123!');
    await page.getByRole('button', { name: 'ログイン', exact: true }).click();
    await page.waitForURL(`${baseUrl}/`);
    await page.goto(`${baseUrl}/settings/passkeys`);
    await page.getByRole('button', { name: 'パスキーを追加' }).click();
    await page.getByText('パスキーを登録しました').waitFor({ timeout: 15000 });
    await page.getByRole('button', { name: 'ログアウト' }).click();
    await page.waitForURL('**/login');
    const negotiation = page.waitForResponse(response => response.url().includes('/hubs/lock/negotiate') && response.ok());
    await page.getByRole('button', { name: 'パスキーでログイン' }).click();
    await page.waitForURL(`${baseUrl}/`, { timeout: 15000 });
    await negotiation;
    console.log('Virtual passkey registration and login succeeded');

    await page.waitForFunction(() => !!window.signalR);
    await page.getByText('デバイスがありません').waitFor();
    const registered = await page.evaluate(async () => {
      const identifier = `smoke-${Date.now()}`;
      const response = await fetch('/api/devices/register', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ devicePublicKey: 'test', deviceId: identifier,
          sesameDeviceUuid: 'test', name: 'Smoke Lock' })
      });
      if (!response.ok) throw new Error(`Registration failed: ${response.status}`);
      return { ...(await response.json()), identifier };
    });
    await page.evaluate(async ({ identifier, activationKey }) => {
      const socket = new WebSocket(`ws://${location.host}/ws/device`);
      window.smokeSocket = socket;
      await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
      socket.send(JSON.stringify({ deviceId: identifier, activationKey }));
      socket.send(JSON.stringify({ type: 'heartbeat', stateConfirmed: true, isLocked: true, batteryLevel: 15, wifiRssi: -50 }));
    }, registered);
    await page.getByText('Smoke Lock').waitFor({ timeout: 10000 });
    await page.getByText('🔒 施錠中').waitFor();
    await page.getByText('電池残量が少ないデバイスがあります。').waitFor();
    await page.evaluate(() => window.smokeSocket.close());
    await page.getByText('Offline', { exact: true }).waitFor({ timeout: 10000 });
    await page.getByText('❔ 状態不明').waitFor();
    console.log('Device heartbeat and disconnect reached the browser via SignalR');
  } finally {
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
