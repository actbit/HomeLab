<script lang="ts">
  import { onMount } from 'svelte';
  import type { SveltePage } from '@sveltesharp/models';

  type Device = {
    id: string;
    name: string;
    status: string | number;
    isLocked: boolean | null;
    batteryLevel?: number | null;
    wifiRssi?: number | null;
    lastSeenAt?: string | null;
  };

  type LogEntry = {
    id: string;
    actionType: string | number;
    triggeredBy?: string | null;
    success: boolean;
    errorMessage?: string | null;
    timestamp: string;
  };

  type BleResult = {
    success: boolean;
    error?: string;
    deviceId?: string;
    publicKey?: string;
  };
  type Passkey = { id: string; name: string | null; authenticatorType: string; createdAt: string; lastUsedAt: string | null };
  type TemporaryKey = { id: string; label: string; remainingUses: number; expiresAtUnix: number; revoked: boolean; registered: boolean };
  type GuestKey = { label: string; deviceName: string; remainingUses: number; expiresAtUnix: number; registered: boolean; online: boolean; isLocked: boolean | null; lastResult: string | null };

  let { model }: { model: SveltePage } = $props();
  let authenticated = $state(model.isAuthenticated);
  let path = $state(model.path || '/');
  let devices = $state<Device[]>([]);
  let selectedDevice = $state<Device | null>(null);
  let logs = $state<LogEntry[]>([]);
  let isLoading = $state(false);
  let busyDeviceId = $state<string | null>(null);
  let errorMessage = $state('');
  let successMessage = $state('');
  let loginEmail = $state('');
  let loginPassword = $state('');
  let loginTotp = $state('');
  let resetEmail = $state('');
  let resetToken = $state('');
  let resetPassword = $state('');
  let resetConfirm = $state('');
  let requiresTotp = $state(false);
  let registerName = $state('');
  let registerEmail = $state('');
  let registerPassword = $state('');
  let registerPasswordConfirm = $state('');
  let provisionStep = $state(1);
  let bleSupported = $state(false);
  let provisionError = $state('');
  let provisionBusy = $state(false);
  let provisionDeviceId = $state('');
  let provisionPublicKey = $state('');
  let provisionWifiSsid = $state('');
  let provisionWifiPassword = $state('');
  let provisionSesameUuid = $state('');
  let provisionSesameApiKey = $state('');
  let provisionDeviceName = $state('');
  let provisionActivationKey = $state('');
  let provisionServerUrl = $state('');
  let totpEnabled = $state(false);
  let totpQrUrl = $state('');
  let totpCode = $state('');
  let recoveryCodes = $state<string[]>([]);
  let passkeys = $state<Passkey[]>([]);
  let passkeySupported = $state(false);
  let settingsBusy = $state(false);
  let counter = $state(0);
  let forecasts = $state<{ date: string; temperature: number; summary: string }[]>([]);
  let notificationPermission = $state('default');
  let realtimeStatus = $state('connecting');
  let temporaryKeys = $state<TemporaryKey[]>([]);
  let keyLabel = $state('');
  let keyUses = $state(1);
  let keyHours = $state(24);
  let shareLink = $state('');
  let guestToken = $state('');
  let guestKey = $state<GuestKey | null>(null);
  let keyBusy = $state(false);

  const page = () => {
    if (path === '/login') return 'login';
    if (path === '/register') return 'register';
    if (path === '/forgot-password') return 'forgot-password';
    if (path === '/reset-password') return 'reset-password';
    if (path === '/temporary-key') return 'temporary-key';
    if (path === '/setup/provision') return 'provision';
    if (path === '/settings/totp') return 'totp';
    if (path === '/settings/passkeys') return 'passkeys';
    if (path === '/counter') return 'counter';
    if (path === '/weather') return 'weather';
    if (path.startsWith('/devices/') && path.length > '/devices/'.length) return 'device';
    if (path === '/devices') return 'devices';
    return 'dashboard';
  };

  function statusLabel(status: string | number) {
    if (typeof status === 'number') return ['Online', 'Offline', 'Unknown'][status] ?? 'Unknown';
    return status;
  }

  function isOnline(status: string | number) {
    return statusLabel(status).toLowerCase() === 'online';
  }

  function lockLabel(state: boolean | null) {
    return state == null ? '❔ 状態不明' : state ? '🔒 施錠中' : '🔓 解錠中';
  }

  function actionLabel(action: string | number) {
    return typeof action === 'number' ? (['施錠', '解錠', 'トグル', '状態確認'][action] ?? '不明') : action;
  }

  function logSource(log: LogEntry) {
    if (!log.triggeredBy?.startsWith('Key:')) return '';
    const key = temporaryKeys.find(item => item.id.replaceAll('-', '').startsWith(log.triggeredBy!.slice(4)));
    return ` · 一時キー: ${key?.label ?? '不明'}`;
  }

  async function enableNotifications() {
    if ('Notification' in window) notificationPermission = await Notification.requestPermission();
  }

  function notifyDeviceChanges(previous: Device[], next: Device[]) {
    if (notificationPermission !== 'granted') return;
    for (const device of next) {
      const old = previous.find(item => item.id === device.id);
      if (!old) continue;
      if (isOnline(old.status) && !isOnline(device.status))
        new Notification('デバイスがオフラインです', { body: device.name });
      if ((old.batteryLevel == null || old.batteryLevel > 20) && device.batteryLevel != null && device.batteryLevel >= 0 && device.batteryLevel <= 20)
        new Notification('電池残量が少なくなっています', { body: `${device.name}: ${device.batteryLevel}%` });
    }
  }

  function setError(message: string) {
    errorMessage = message;
    successMessage = '';
  }

  function setSuccess(message: string) {
    successMessage = message;
    errorMessage = '';
  }

  async function readError(response: Response, fallback: string) {
    const text = await response.text();
    try {
      const body = JSON.parse(text);
      return body.error || body.title || fallback;
    } catch {
      return fallback;
    }
  }

  async function loadDevices() {
    if (!authenticated) return;
    isLoading = true;
    try {
      const response = await fetch('/api/devices', { credentials: 'include' });
      if (!response.ok) throw new Error(await readError(response, 'デバイス一覧を取得できませんでした'));
      const next: Device[] = await response.json();
      notifyDeviceChanges(devices, next);
      devices = next;
    } catch (error) {
      setError(error instanceof Error ? error.message : 'デバイス一覧の取得に失敗しました');
    } finally {
      isLoading = false;
    }
  }

  async function loadDevice(id: string) {
    isLoading = true;
    try {
      const [deviceResponse, logsResponse] = await Promise.all([
        fetch(`/api/devices/${id}`, { credentials: 'include' }),
        fetch(`/api/devices/${id}/logs`, { credentials: 'include' })
      ]);
      if (!deviceResponse.ok) throw new Error(await readError(deviceResponse, 'デバイスを取得できませんでした'));
      selectedDevice = await deviceResponse.json();
      logs = logsResponse.ok ? await logsResponse.json() : [];
      const keysResponse = await fetch(`/api/devices/${id}/temporary-keys`, { credentials: 'include' });
      temporaryKeys = keysResponse.ok ? await keysResponse.json() : [];
    } catch (error) {
      setError(error instanceof Error ? error.message : 'デバイス詳細の取得に失敗しました');
    } finally {
      isLoading = false;
    }
  }

  async function createTemporaryKey(event: SubmitEvent) {
    event.preventDefault();
    if (!selectedDevice) return;
    keyBusy = true;
    try {
      const response = await fetch(`/api/devices/${selectedDevice.id}/temporary-keys`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ label: keyLabel, uses: Number(keyUses), validHours: Number(keyHours) })
      });
      if (!response.ok) throw new Error(await readError(response, '一時キーを作成できませんでした'));
      const result = await response.json();
      shareLink = `${window.location.origin}/temporary-key#${result.token}`;
      keyLabel = '';
      await loadDevice(selectedDevice.id);
      setSuccess('一時キーを作成しました。リンクはこの画面に表示されている間に共有してください。');
    } catch (error) { setError(error instanceof Error ? error.message : '一時キーを作成できませんでした'); }
    finally { keyBusy = false; }
  }

  async function revokeTemporaryKey(key: TemporaryKey) {
    if (!selectedDevice || !window.confirm(`「${key.label}」を無効にしますか？`)) return;
    keyBusy = true;
    try {
      const response = await fetch(`/api/devices/${selectedDevice.id}/temporary-keys/${key.id}`, { method: 'DELETE', credentials: 'include' });
      if (!response.ok) throw new Error('一時キーを無効にできませんでした');
      await loadDevice(selectedDevice.id);
      setSuccess('一時キーを無効にしました');
    } catch (error) { setError(error instanceof Error ? error.message : '一時キーを無効にできませんでした'); }
    finally { keyBusy = false; }
  }

  async function loadGuestKey() {
    if (!guestToken) { setError('共有リンクを開いてください。'); return; }
    try {
      const response = await fetch('/api/temporary-keys/status', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token: guestToken }) });
      if (!response.ok) throw new Error(await readError(response, '一時キーが無効か期限切れです。'));
      guestKey = await response.json();
    } catch (error) { guestKey = null; setError(error instanceof Error ? error.message : '一時キーを取得できませんでした'); }
  }

  async function useGuestKey(action: 'register' | 'unlock') {
    keyBusy = true;
    try {
      await (window as any).passkeyAuth.temporary(guestToken, action);
      if (action === 'register') {
        localStorage.setItem('homelab.temporaryKey', guestToken);
        setSuccess('パスキーを登録しました。この端末で一時キーを利用できます。');
      } else setSuccess('解錠要求を送信しました。デバイスからの結果を待っています。');
      await loadGuestKey();
    } catch (error) { setError(error instanceof Error ? error.message : '一時キーを利用できませんでした'); await loadGuestKey(); }
    finally { keyBusy = false; }
  }

  async function startGuestSignalR() {
    const signalR = (window as any).signalR;
    if (!signalR || !guestToken) return;
    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/temporary-key').withAutomaticReconnect().build();
    connection.on('Updated', loadGuestKey);
    connection.onreconnected(async () => { try { await connection.invoke('Join', guestToken); await loadGuestKey(); } catch { await loadGuestKey(); } });
    let disposed = false;
    let retryTimer: number | undefined;
    const connect = async () => {
      if (disposed) return;
      try { await connection.start(); await connection.invoke('Join', guestToken); await loadGuestKey(); }
      catch {
        await loadGuestKey();
        if (connection.state !== signalR.HubConnectionState.Disconnected) await connection.stop();
        if (!disposed && guestKey) { if (retryTimer !== undefined) clearTimeout(retryTimer); retryTimer = window.setTimeout(connect, 5000); }
      }
    };
    connection.onclose(() => { if (!disposed && guestKey) retryTimer = window.setTimeout(connect, 5000); });
    window.addEventListener('pagehide', () => { disposed = true; if (retryTimer !== undefined) clearTimeout(retryTimer); void connection.stop(); }, { once: true });
    await connect();
  }

  async function operate(deviceId: string, action: 'lock' | 'unlock' | 'toggle') {
    busyDeviceId = deviceId;
    errorMessage = '';
    successMessage = '';
    const actionValue = action === 'lock' ? 0 : action === 'unlock' ? 1 : 2;

    try {
      const response = await fetch(`/api/locks/${deviceId}/action`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action: actionValue })
      });
      if (!response.ok) throw new Error(await readError(response, 'ロック操作に失敗しました'));
      setSuccess('操作を受け付けました。デバイスからの結果を待っています。');
      await loadDevices();
      if (page() === 'device') await loadDevice(deviceId);
    } catch (error) {
      setError(error instanceof Error ? error.message : 'ロック操作に失敗しました');
    } finally {
      busyDeviceId = null;
    }
  }

  async function submitLogin(event: SubmitEvent) {
    event.preventDefault();
    isLoading = true;
    try {
      const response = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: loginEmail, password: loginPassword, totpCode: loginTotp || null })
      });
      if (!response.ok) {
        const message = await readError(response, 'メールアドレスまたはパスワードが正しくありません');
        if (message.toLowerCase().includes('totp') || message.includes('認証コード')) {
          requiresTotp = true;
          loginTotp = '';
        }
        throw new Error(message);
      }
      window.location.href = '/';
    } catch (error) {
      setError(error instanceof Error ? error.message : 'ログインに失敗しました');
    } finally {
      isLoading = false;
    }
  }

  async function submitRegister(event: SubmitEvent) {
    event.preventDefault();
    if (registerPassword !== registerPasswordConfirm) {
      setError('パスワードが一致しません');
      return;
    }
    isLoading = true;
    try {
      const response = await fetch('/api/auth/register', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: registerEmail, password: registerPassword, displayName: registerName })
      });
      if (!response.ok) throw new Error(await readError(response, 'アカウント作成に失敗しました'));
      setSuccess('アカウントを作成しました。ログインしてください。');
      window.setTimeout(() => { window.location.href = '/login'; }, 500);
    } catch (error) {
      setError(error instanceof Error ? error.message : 'アカウント作成に失敗しました');
    } finally {
      isLoading = false;
    }
  }

  async function requestPasswordReset(event: SubmitEvent) {
    event.preventDefault();
    isLoading = true;
    try {
      const response = await fetch('/api/auth/forgot-password', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: resetEmail })
      });
      if (!response.ok) throw new Error(response.status === 503 ? 'メール送信の設定が必要です。管理者に連絡してください。' : '再設定を開始できませんでした');
      setSuccess('アカウントが存在する場合、再設定用メールを送信します。');
    } catch (error) { setError(error instanceof Error ? error.message : '再設定を開始できませんでした'); }
    finally { isLoading = false; }
  }

  async function submitPasswordReset(event: SubmitEvent) {
    event.preventDefault();
    if (resetPassword !== resetConfirm) { setError('パスワードが一致しません'); return; }
    isLoading = true;
    try {
      const response = await fetch('/api/auth/reset-password', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: resetEmail, token: resetToken, password: resetPassword })
      });
      if (!response.ok) throw new Error('リンクが無効か、パスワードが要件を満たしていません。');
      resetToken = '';
      window.history.replaceState(null, '', '/reset-password');
      setSuccess('パスワードを変更しました。ログインしてください。');
    } catch (error) { setError(error instanceof Error ? error.message : 'パスワードを変更できませんでした'); }
    finally { isLoading = false; }
  }

  async function logout() {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'include' });
    window.location.href = '/login';
  }

  async function loadSettings() {
    settingsBusy = true;
    try {
      const response = await fetch(page() === 'totp' ? '/api/auth/totp/status' : '/api/auth/passkeys', { credentials: 'include' });
      if (!response.ok) throw new Error(await readError(response, '設定を取得できませんでした'));
      const result = await response.json();
      if (page() === 'totp') totpEnabled = result.enabled;
      else passkeys = result;
    } catch (error) { setError(error instanceof Error ? error.message : '設定を取得できませんでした'); }
    finally { settingsBusy = false; }
  }

  async function setupTotp() {
    settingsBusy = true;
    try {
      const response = await fetch('/api/auth/totp/setup', { method: 'POST', credentials: 'include' });
      if (!response.ok) throw new Error(await readError(response, 'セットアップに失敗しました'));
      if (totpQrUrl) URL.revokeObjectURL(totpQrUrl);
      totpQrUrl = URL.createObjectURL(await response.blob());
      errorMessage = '';
    } catch (error) { setError(error instanceof Error ? error.message : 'セットアップに失敗しました'); }
    finally { settingsBusy = false; }
  }

  async function changeTotp(enable: boolean) {
    if ((enable && !/^\d{6}$/.test(totpCode)) || (!enable && !totpCode.trim())) { setError('認証コードを入力してください'); return; }
    settingsBusy = true;
    try {
      const response = await fetch(`/api/auth/totp/${enable ? 'enable' : 'disable'}`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: totpCode })
      });
      if (!response.ok) throw new Error(await readError(response, '認証コードを確認できませんでした'));
      const result = await response.json();
      recoveryCodes = enable ? (result.recoveryCodes || []) : [];
      totpEnabled = enable;
      totpCode = '';
      if (totpQrUrl) URL.revokeObjectURL(totpQrUrl);
      totpQrUrl = '';
      setSuccess(enable ? '認証器を有効にしました' : '認証器を無効にしました');
    } catch (error) { setError(error instanceof Error ? error.message : '設定を変更できませんでした'); }
    finally { settingsBusy = false; }
  }

  async function deletePasskey(key: Passkey) {
    if (!window.confirm(`「${key.name || 'パスキー'}」を削除しますか？`)) return;
    settingsBusy = true;
    try {
      const response = await fetch(`/api/auth/passkeys/${key.id}`, { method: 'DELETE', credentials: 'include' });
      if (!response.ok) throw new Error(await readError(response, '削除に失敗しました'));
      passkeys = passkeys.filter(item => item.id !== key.id);
      setSuccess('パスキーを削除しました');
    } catch (error) { setError(error instanceof Error ? error.message : '削除に失敗しました'); }
    finally { settingsBusy = false; }
  }

  async function registerPasskey() {
    settingsBusy = true;
    try {
      const result = await (window as any).passkeyAuth.register('ブラウザ');
      if (!result.success) throw new Error(result.error);
      await loadSettings();
      setSuccess('パスキーを登録しました');
    } catch (error) { setError(error instanceof Error ? error.message : 'パスキーを登録できませんでした'); }
    finally { settingsBusy = false; }
  }

  async function loginWithPasskey() {
    isLoading = true;
    try {
      const result = await (window as any).passkeyAuth.login();
      if (!result.success) throw new Error(result.error);
      window.location.href = '/';
    } catch (error) { setError(error instanceof Error ? error.message : 'パスキーでログインできませんでした'); }
    finally { isLoading = false; }
  }

  async function deleteDevice(device: Device) {
    if (!window.confirm(`「${device.name}」を削除しますか？`)) return;
    busyDeviceId = device.id;
    try {
      const response = await fetch(`/api/devices/${device.id}`, { method: 'DELETE', credentials: 'include' });
      if (!response.ok) throw new Error(await readError(response, 'デバイスを削除できませんでした'));
      await loadDevices();
      setSuccess('デバイスを削除しました');
    } catch (error) { setError(error instanceof Error ? error.message : 'デバイスを削除できませんでした'); }
    finally { busyDeviceId = null; }
  }

  async function bleCall(method: string, ...args: unknown[]): Promise<BleResult> {
    const ble = (window as any).bleProvisioning;
    if (!ble || typeof ble[method] !== 'function') return { success: false, error: 'Web Bluetoothを利用できません' };
    return await ble[method](...args);
  }

  async function connectBle() {
    provisionBusy = true;
    provisionError = '';
    try {
      const result = await bleCall('connect');
      if (!result.success) throw new Error(result.error || 'BLE接続に失敗しました');
      provisionDeviceId = result.deviceId || '';
      provisionPublicKey = result.publicKey || '';
      provisionStep = 2;
    } catch (error) {
      provisionError = error instanceof Error ? error.message : 'BLE接続に失敗しました';
    } finally { provisionBusy = false; }
  }

  async function writeWifi() {
    if (!provisionWifiSsid || !provisionWifiPassword) { provisionError = 'SSIDとパスワードを入力してください'; return; }
    provisionBusy = true;
    provisionError = '';
    try {
      const result = await bleCall('writeWifiCredentials', provisionWifiSsid, provisionWifiPassword);
      if (!result.success) throw new Error(result.error || 'WiFi設定の書き込みに失敗しました');
      provisionStep = 3;
    } catch (error) {
      provisionError = error instanceof Error ? error.message : 'WiFi設定に失敗しました';
    } finally { provisionBusy = false; }
  }

  async function writeSesame() {
    if (!provisionSesameUuid) { provisionError = 'SESAME UUIDを入力してください'; return; }
    provisionBusy = true;
    provisionError = '';
    try {
      const result = await bleCall('writeSesameInfo', provisionSesameUuid, provisionSesameApiKey);
      if (!result.success) throw new Error(result.error || 'SESAME設定の書き込みに失敗しました');
      provisionStep = 4;
    } catch (error) {
      provisionError = error instanceof Error ? error.message : 'SESAME設定に失敗しました';
    } finally { provisionBusy = false; }
  }

  async function registerProvisionedDevice() {
    if (!provisionDeviceName) { provisionError = 'デバイス名を入力してください'; return; }
    provisionBusy = true;
    provisionError = '';
    try {
      const response = await fetch('/api/devices/register', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          devicePublicKey: provisionPublicKey,
          deviceId: provisionDeviceId,
          sesameDeviceUuid: provisionSesameUuid,
          sesameApiKey: null,
          name: provisionDeviceName
        })
      });
      if (!response.ok) throw new Error(await readError(response, 'デバイス登録に失敗しました'));
      const result = await response.json();
      provisionActivationKey = result.activationKey;
      provisionServerUrl = result.serverUrl;
      provisionStep = 5;
    } catch (error) {
      provisionError = error instanceof Error ? error.message : 'デバイス登録に失敗しました';
    } finally { provisionBusy = false; }
  }

  async function writeProvisioningInfo() {
    provisionBusy = true;
    provisionError = '';
    try {
      const activationResult = await bleCall('writeActivationKey', provisionActivationKey);
      if (!activationResult.success) throw new Error(activationResult.error || 'アクティベーションキーの書き込みに失敗しました');
      const serverResult = await bleCall('writeServerUrl', provisionServerUrl);
      if (!serverResult.success) throw new Error(serverResult.error || 'サーバーURLの書き込みに失敗しました');
      provisionStep = 6;
    } catch (error) {
      provisionError = error instanceof Error ? error.message : 'ESP32への書き込みに失敗しました';
    } finally { provisionBusy = false; }
  }

  async function startSignalR() {
    const signalR = (window as any).signalR;
    if (!signalR || !authenticated) { realtimeStatus = 'disconnected'; return; }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/lock')
      .withAutomaticReconnect()
      .build();

    connection.on('LockStateChanged', async () => {
      await loadDevices();
      if (selectedDevice) await loadDevice(selectedDevice.id);
    });
    connection.on('DeviceStatusChanged', loadDevices);
    connection.on('CommandResult', async () => {
      await loadDevices();
      if (selectedDevice) await loadDevice(selectedDevice.id);
    });
    let disposed = false;
    let retryTimer: number | undefined;
    const connect = async () => {
      if (disposed) return;
      try {
        realtimeStatus = 'connecting';
        await connection.start();
        realtimeStatus = 'connected';
        await loadDevices();
        if (selectedDevice) await loadDevice(selectedDevice.id);
      } catch (error) {
        realtimeStatus = 'disconnected';
        console.warn('SignalR connection failed', error);
        if (!disposed) retryTimer = window.setTimeout(connect, 5000);
      }
    };
    connection.onreconnecting(() => { realtimeStatus = 'connecting'; });
    connection.onclose(() => {
      realtimeStatus = 'disconnected';
      if (!disposed) retryTimer = window.setTimeout(connect, 5000);
    });
    connection.onreconnected(async () => {
      try {
        realtimeStatus = 'connected';
        await loadDevices();
        if (selectedDevice) await loadDevice(selectedDevice.id);
      } catch (error) {
        console.warn('SignalR rejoin failed', error);
      }
    });
    window.addEventListener('pagehide', () => {
      disposed = true;
      if (retryTimer !== undefined) clearTimeout(retryTimer);
      void connection.stop();
    }, { once: true });
    await connect();
  }

  onMount(async () => {
    if ('Notification' in window) notificationPermission = Notification.permission;
    passkeySupported = Boolean((window as any).passkeyAuth?.isSupported());
    if (page() === 'temporary-key') {
      const incoming = window.location.hash.slice(1);
      if (incoming) sessionStorage.setItem('homelab.pendingTemporaryKey', incoming);
      guestToken = incoming || sessionStorage.getItem('homelab.pendingTemporaryKey') || localStorage.getItem('homelab.temporaryKey') || '';
      window.history.replaceState(null, '', '/temporary-key');
      await loadGuestKey();
      if (guestKey) await startGuestSignalR();
      return;
    }
    if (page() === 'reset-password') {
      const params = new URLSearchParams(window.location.hash.slice(1));
      resetEmail = params.get('email') || '';
      resetToken = params.get('token') || '';
      window.history.replaceState(null, '', '/reset-password');
    }
    if (page() === 'weather') {
      const summaries = ['Freezing', 'Bracing', 'Chilly', 'Cool', 'Mild', 'Warm', 'Balmy', 'Hot', 'Sweltering', 'Scorching'];
      forecasts = Array.from({ length: 5 }, (_, index) => {
        const date = new Date();
        date.setDate(date.getDate() + index + 1);
        return { date: date.toLocaleDateString(), temperature: Math.floor(Math.random() * 75) - 20, summary: summaries[Math.floor(Math.random() * summaries.length)] };
      });
    }
    if (authenticated) {
      if (page() === 'device') {
        const id = path.split('/').pop();
        if (id) await loadDevice(id);
      } else if (page() === 'provision') {
        bleSupported = Boolean((window as any).bleProvisioning?.isSupported?.());
      } else if (page() === 'totp' || page() === 'passkeys') {
        await loadSettings();
      } else {
        await loadDevices();
      }
      if (page() !== 'provision') await startSignalR();
    } else if (page() === 'provision') {
      bleSupported = Boolean((window as any).bleProvisioning?.isSupported?.());
    }
  });
</script>

<svelte:head>
  <title>HomeLab</title>
  <script src="/js/ble-provisioning.js"></script>
  <script src="/js/passkey.js"></script>
  <script src="/lib/signalr/signalr.min.js"></script>
</svelte:head>

{#if page() === 'counter'}
  <main class="content"><section class="panel"><h1>Counter</h1><p role="status">Current count: {counter}</p><button class="primary" onclick={() => counter++}>Click me</button></section></main>
{:else if page() === 'weather'}
  <main class="content"><section class="panel"><h1>Weather</h1><p>Sample forecast data</p>{#if forecasts.length}<table><thead><tr><th>Date</th><th>Temp. (C)</th><th>Temp. (F)</th><th>Summary</th></tr></thead><tbody>{#each forecasts as forecast}<tr><td>{forecast.date}</td><td>{forecast.temperature}</td><td>{32 + Math.trunc(forecast.temperature / 0.5556)}</td><td>{forecast.summary}</td></tr>{/each}</tbody></table>{:else}<p>Loading...</p>{/if}</section></main>
{:else if page() === 'temporary-key'}
  <main class="auth-shell"><section class="auth-card"><div class="brand">🏠 HomeLab</div><h1>一時キー</h1>
    {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}{#if successMessage}<div class="alert success">{successMessage}</div>{/if}
    {#if guestKey}
      <h2>{guestKey.deviceName}</h2><p>{guestKey.label} · 残り {guestKey.remainingUses} 回</p>
      <p class="muted">有効期限 {new Date(guestKey.expiresAtUnix * 1000).toLocaleString('ja-JP')}</p>
      <p>{guestKey.online ? lockLabel(guestKey.isLocked) : 'オフライン'}</p>
      {#if guestKey.lastResult}<p>前回の操作: {guestKey.lastResult === 'pending' ? '確認中' : guestKey.lastResult === 'success' ? '成功' : '失敗'}</p>{/if}
      {#if !passkeySupported}<p class="alert error">パスキーにはHTTPSと対応ブラウザが必要です。</p>
      {:else if !guestKey.registered}<button class="primary" disabled={keyBusy || guestKey.remainingUses <= 0} onclick={() => useGuestKey('register')}>パスキーを登録</button>
      {:else}<button class="success-button" disabled={keyBusy || guestKey.remainingUses <= 0 || !guestKey.online} onclick={() => useGuestKey('unlock')}>パスキーで解錠</button>{/if}
      <p class="muted">解錠要求の送信時に1回消費します。結果を確認できない場合も回数は戻りません。</p>
    {/if}
  </section></main>
{:else if !authenticated && page() === 'login'}
  <main class="auth-shell">
    <section class="auth-card">
      <div class="brand">🏠 HomeLab</div>
      <p class="muted">SESAMEスマートロック管理</p>
      <h1>ログイン</h1>
      {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}
      <form onsubmit={submitLogin}>
        <label>メールアドレス<input type="email" bind:value={loginEmail} required autocomplete="email" /></label>
        <label>パスワード<input type="password" bind:value={loginPassword} required autocomplete="current-password" /></label>
        {#if requiresTotp}<label>認証コードまたはリカバリーコード<input bind:value={loginTotp} required autocomplete="one-time-code" /></label>{/if}
        <button class="primary" disabled={isLoading}>{isLoading ? 'ログイン中...' : 'ログイン'}</button>
      </form>
      {#if passkeySupported}<p class="switch"><button class="secondary" disabled={isLoading} onclick={loginWithPasskey}>パスキーでログイン</button></p>{/if}
      <p class="switch"><a href="/forgot-password">パスワードを忘れた場合</a></p>
      <p class="switch">アカウントがありませんか？ <a href="/register">作成する</a></p>
    </section>
  </main>
{:else if page() === 'forgot-password'}
  <main class="auth-shell"><section class="auth-card"><div class="brand">🏠 HomeLab</div><h1>パスワード再設定</h1>
    {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}{#if successMessage}<div class="alert success">{successMessage}</div>{/if}
    <form onsubmit={requestPasswordReset}><label>メールアドレス<input type="email" bind:value={resetEmail} required autocomplete="email" /></label><button class="primary" disabled={isLoading}>再設定メールを送信</button></form><p class="switch"><a href="/login">ログインへ戻る</a></p>
  </section></main>
{:else if page() === 'reset-password'}
  <main class="auth-shell"><section class="auth-card"><div class="brand">🏠 HomeLab</div><h1>新しいパスワード</h1>
    {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}{#if successMessage}<div class="alert success">{successMessage}</div>{/if}
    {#if !successMessage}<form onsubmit={submitPasswordReset}><label>新しいパスワード<input type="password" bind:value={resetPassword} required minlength="8" autocomplete="new-password" /></label><label>確認<input type="password" bind:value={resetConfirm} required minlength="8" autocomplete="new-password" /></label><button class="primary" disabled={isLoading || !resetToken}>変更する</button></form>{/if}<p class="switch"><a href="/login">ログインへ戻る</a></p>
  </section></main>
{:else if !authenticated && page() === 'register'}
  <main class="auth-shell">
    <section class="auth-card">
      <div class="brand">🏠 HomeLab</div>
      <h1>アカウント作成</h1>
      {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}
      {#if successMessage}<div class="alert success">{successMessage}</div>{/if}
      <form onsubmit={submitRegister}>
        <label>表示名<input bind:value={registerName} required /></label>
        <label>メールアドレス<input type="email" bind:value={registerEmail} required autocomplete="email" /></label>
        <label>パスワード<input type="password" bind:value={registerPassword} required minlength="8" autocomplete="new-password" /></label>
        <label>パスワード確認<input type="password" bind:value={registerPasswordConfirm} required minlength="8" autocomplete="new-password" /></label>
        <button class="primary" disabled={isLoading}>{isLoading ? '作成中...' : 'アカウント作成'}</button>
      </form>
      <p class="switch"><a href="/login">ログインへ戻る</a></p>
    </section>
  </main>
{:else if !authenticated}
  <main class="auth-shell">
    <section class="auth-card center">
      <div class="brand">🏠 HomeLab</div>
      <h1>スマートロックを安全に管理</h1>
      <p class="muted">ログインしてデバイスを確認してください。</p>
      <a class="button primary" href="/login">ログイン</a>
      <a class="button secondary" href="/register">アカウント作成</a>
    </section>
  </main>
{:else}
  <div class="app-shell">
    <header class="topbar">
      <a class="brand" href="/">🏠 HomeLab</a>
      <nav>
        <a class:active={page() === 'dashboard'} href="/">ダッシュボード</a>
        <a class:active={page() === 'devices'} href="/devices">デバイス</a>
        <a href="/setup/provision">デバイス追加</a>
        <a href="/settings/totp">認証器</a>
        <a href="/settings/passkeys">パスキー</a>
      </nav>
      <div class="account">{page() !== 'provision' ? (realtimeStatus === 'connected' ? '● 接続中' : realtimeStatus === 'connecting' ? '◌ 接続中…' : '○ 更新停止') : ''} {model.userName ?? 'ユーザー'} <button onclick={logout}>ログアウト</button></div>
    </header>

    <main class="content">
      {#if errorMessage}<div class="alert error">{errorMessage}</div>{/if}
      {#if successMessage}<div class="alert success">{successMessage}</div>{/if}
      {#if authenticated && page() === 'dashboard' && notificationPermission === 'default'}<div class="alert">接続断や電池残量低下をブラウザ通知できます。 <button class="secondary" onclick={enableNotifications}>通知を有効にする</button></div>{/if}
      {#if authenticated && page() === 'dashboard' && devices.some(d => d.batteryLevel != null && d.batteryLevel >= 0 && d.batteryLevel <= 20)}<div class="alert error">電池残量が少ないデバイスがあります。</div>{/if}

      {#if page() === 'totp'}
        <div class="page-heading"><div><p class="eyebrow">設定</p><h1>認証器（TOTP）</h1></div></div>
        <section class="panel provision-panel">
          <p class="muted">認証器アプリの6桁のコードを使ってログインを保護します。</p>
          {#if totpEnabled}
            <h2>認証器は有効です</h2><label>無効化するには認証コードまたはリカバリーコードを入力<input bind:value={totpCode} autocomplete="one-time-code" /></label>
            {#if recoveryCodes.length}<div class="alert"><strong>リカバリーコードを安全な場所に保存してください。各コードは一度だけ使えます。</strong><ul>{#each recoveryCodes as code}<li><code>{code}</code></li>{/each}</ul></div>{/if}
            <button class="danger-button" disabled={settingsBusy} onclick={() => changeTotp(false)}>無効にする</button>
          {:else if totpQrUrl}
            <h2>認証器アプリでQRコードを読み取ってください</h2><img src={totpQrUrl} alt="認証器登録用QRコード" width="240" height="240" />
            <label>認証コード<input bind:value={totpCode} inputmode="numeric" maxlength="6" autocomplete="one-time-code" /></label><button class="primary" disabled={settingsBusy} onclick={() => changeTotp(true)}>有効にする</button>
          {:else}
            <h2>認証器は無効です</h2><button class="primary" disabled={settingsBusy} onclick={setupTotp}>セットアップ開始</button>
          {/if}
        </section>
      {:else if page() === 'passkeys'}
        <div class="page-heading"><div><p class="eyebrow">設定</p><h1>パスキー管理</h1></div></div>
        <section class="panel">
          <p class="muted">パスキーを使うと、生体認証やデバイスのPINでログインできます。</p>
          {#if passkeySupported}<button class="primary" disabled={settingsBusy} onclick={registerPasskey}>パスキーを追加</button>{:else}<p class="muted">パスキー登録にはHTTPSと対応ブラウザが必要です。</p>{/if}
          {#if passkeys.length}<div class="device-list">{#each passkeys as key}<article class="device-row"><div class="device-main"><h2>{key.name || '名前なし'}</h2><p>{key.authenticatorType} · 登録 {new Date(key.createdAt).toLocaleDateString('ja-JP')}{key.lastUsedAt ? ` · 最終使用 ${new Date(key.lastUsedAt).toLocaleDateString('ja-JP')}` : ''}</p></div><button class="danger-button" disabled={settingsBusy} onclick={() => deletePasskey(key)}>削除</button></article>{/each}</div>{:else if !settingsBusy}<p>パスキーは登録されていません。</p>{/if}
        </section>
      {:else if page() === 'provision'}
        <div class="page-heading"><div><p class="eyebrow">セットアップ</p><h1>ESP32 デバイス初期設定</h1></div><a class="button secondary" href="/devices">キャンセル</a></div>
        <section class="panel provision-panel">
          <div class="stepper">{#each ['BLE接続', 'WiFi設定', 'SESAME設定', 'サーバー登録', '書き込み', '完了'] as label, index}<span class:active={provisionStep === index + 1} class:completed={provisionStep > index + 1}>{index + 1}. {label}</span>{/each}</div>
          {#if provisionError}<div class="alert error">{provisionError}</div>{/if}
          {#if provisionStep === 1}
            <h2>ESP32に接続</h2><p class="muted">ESP32の電源を入れてBLEモードにし、接続してください。ペアリングを求められたらシリアルモニターに表示される6桁のコードを入力します。</p>
            {#if !bleSupported}<div class="alert error">Web Bluetoothに対応したChromeまたはEdgeで開いてください。</div>{/if}
            <button class="primary" disabled={!bleSupported || provisionBusy} onclick={connectBle}>{provisionBusy ? '接続中...' : 'ESP32に接続'}</button>
          {:else if provisionStep === 2}
            <h2>WiFi設定</h2><p class="muted">ESP32が接続するWiFi情報を入力してください。</p>
            <label>WiFi SSID<input bind:value={provisionWifiSsid} required /></label><label>WiFiパスワード<input type="password" bind:value={provisionWifiPassword} required /></label>
            <button class="primary" disabled={provisionBusy} onclick={writeWifi}>次へ</button>
          {:else if provisionStep === 3}
            <h2>SESAME設定</h2><p class="muted">SESAMEロックのUUIDとAPI有効化キーを入力してください。</p>
            <label>SESAMEデバイスUUID<input bind:value={provisionSesameUuid} required /></label><label>SESAME API有効化キー<input bind:value={provisionSesameApiKey} /></label>
            <button class="primary" disabled={provisionBusy} onclick={writeSesame}>次へ</button>
          {:else if provisionStep === 4}
            <h2>サーバーにデバイス登録</h2><p class="muted">登録後、ESP32用のアクティベーションキーを発行します。</p>
            <label>デバイス名<input bind:value={provisionDeviceName} placeholder="例: 玄関の鍵" required /></label>
            <button class="primary" disabled={provisionBusy} onclick={registerProvisionedDevice}>登録して次へ</button>
          {:else if provisionStep === 5}
            <h2>ESP32に設定を書き込み</h2><p class="muted">アクティベーションキーとサーバーの接続先をESP32へ送ります。</p>
            <dl><div><dt>アクティベーションキー</dt><dd class="mono">{provisionActivationKey}</dd></div><div><dt>サーバーURL</dt><dd class="mono">{provisionServerUrl}</dd></div></dl>
            <button class="primary" disabled={provisionBusy} onclick={writeProvisioningInfo}>ESP32に書き込む</button>
          {:else}
            <h2>✅ 設定完了</h2><p class="muted">ESP32がWiFiとサーバーへ接続するまで数十秒お待ちください。</p><a class="button primary" href="/devices">デバイス一覧へ</a>
          {/if}
        </section>
      {:else if page() === 'device' && selectedDevice}
        <a class="back" href="/devices">← デバイス一覧</a>
        <div class="page-heading"><div><p class="eyebrow">デバイス詳細</p><h1>{selectedDevice.name}</h1></div><span class:online={isOnline(selectedDevice.status)} class="status">{statusLabel(selectedDevice.status)}</span></div>
        <section class="detail-grid">
          <article class="panel state-panel">
            <p class="eyebrow">現在の状態</p>
            <div class="lock-state">{lockLabel(selectedDevice.isLocked)}</div>
            <div class="actions">
              <button class="success-button" disabled={busyDeviceId === selectedDevice.id} onclick={() => operate(selectedDevice!.id, 'unlock')}>解錠</button>
              <button class="danger-button" disabled={busyDeviceId === selectedDevice.id} onclick={() => operate(selectedDevice!.id, 'lock')}>施錠</button>
              <button class="secondary" disabled={busyDeviceId === selectedDevice.id || selectedDevice.isLocked == null} onclick={() => operate(selectedDevice!.id, 'toggle')}>トグル</button>
            </div>
            <dl><div><dt>バッテリー</dt><dd>{selectedDevice.batteryLevel ?? '-'}{selectedDevice.batteryLevel == null ? '' : '%'}</dd></div><div><dt>Wi‑Fi</dt><dd>{selectedDevice.wifiRssi ?? '-'}{selectedDevice.wifiRssi == null ? '' : ' dBm'}</dd></div><div><dt>最終通信</dt><dd>{selectedDevice.lastSeenAt ? new Date(selectedDevice.lastSeenAt).toLocaleString('ja-JP') : '未接続'}</dd></div></dl>
          </article>
          <article class="panel"><div class="panel-heading"><h2>操作履歴</h2><a href={path}>更新</a></div>{#if logs.length}<div class="log-list">{#each logs as log}<div class:failed={!log.success && log.errorMessage !== 'Pending'} class="log-row"><span>{new Date(log.timestamp).toLocaleString('ja-JP')}</span><strong>{actionLabel(log.actionType)}{logSource(log)}</strong><span>{log.errorMessage === 'Pending' ? '確認中' : log.errorMessage === 'Timed out' ? 'タイムアウト' : log.success ? '成功' : '失敗'}</span></div>{/each}</div>{:else}<p class="muted">操作履歴がありません。</p>{/if}</article>
          <article class="panel"><h2>一時キーを共有</h2><p class="muted">リンクを受け取った人がパスキーを登録し、指定回数だけ解錠できます。</p>
            <form onsubmit={createTemporaryKey}><label>キーの名前<input bind:value={keyLabel} maxlength="100" required placeholder="例: 来客用" /></label><label>解錠できる回数<input type="number" min="1" max="1000" bind:value={keyUses} required /></label><label>有効時間（時間）<input type="number" min="1" max="720" bind:value={keyHours} required /></label><button class="primary" disabled={keyBusy}>一時キーを作成</button></form>
            {#if shareLink}<label>共有リンク（作成直後のみ表示）<input value={shareLink} readonly onclick={(event) => (event.currentTarget as HTMLInputElement).select()} /></label><button class="secondary" onclick={() => navigator.clipboard.writeText(shareLink)}>リンクをコピー</button>{/if}
            {#if temporaryKeys.length}<div class="log-list">{#each temporaryKeys as key}<div class="device-row"><div class="device-main"><h2>{key.label}</h2><p>{key.revoked ? '無効' : key.expiresAtUnix * 1000 <= Date.now() ? '期限切れ' : `残り ${key.remainingUses} 回`} · {key.registered ? 'パスキー登録済み' : '未登録'} · {new Date(key.expiresAtUnix * 1000).toLocaleString('ja-JP')}まで</p></div>{#if !key.revoked}<button class="danger-button" disabled={keyBusy} onclick={() => revokeTemporaryKey(key)}>無効化</button>{/if}</div>{/each}</div>{/if}
          </article>
        </section>
      {:else if page() === 'devices'}
        <div class="page-heading"><div><p class="eyebrow">管理</p><h1>デバイス</h1></div><a class="button primary" href="/setup/provision">＋ デバイス追加</a></div>
        {#if isLoading}<p class="muted">読み込み中...</p>{:else if devices.length}<section class="device-list">{#each devices as device}<article class="device-row"><div class="device-icon">{device.isLocked == null ? '❔' : device.isLocked ? '🔒' : '🔓'}</div><div class="device-main"><a href={`/devices/${device.id}`}><h2>{device.name}</h2></a><p>{statusLabel(device.status)} · 最終通信 {device.lastSeenAt ? new Date(device.lastSeenAt).toLocaleString('ja-JP') : '未接続'}</p></div><div class="device-meta"><span>{device.batteryLevel == null ? '-' : `${device.batteryLevel}%`}</span><span>{device.wifiRssi == null ? '-' : `${device.wifiRssi} dBm`}</span></div><a class="button secondary" href={`/devices/${device.id}`}>詳細</a><button class="danger-button" disabled={busyDeviceId === device.id} onclick={() => deleteDevice(device)}>削除</button></article>{/each}</section>{:else}<section class="empty panel"><h2>デバイスがありません</h2><p>ESP32を接続して、最初のデバイスを追加してください。</p><a class="button primary" href="/setup/provision">デバイスを追加</a></section>{/if}
      {:else}
        <div class="page-heading"><div><p class="eyebrow">ようこそ、{model.userName ?? 'ユーザー'}さん</p><h1>ダッシュボード</h1></div><button class="secondary" onclick={loadDevices}>↻ 更新</button></div>
        {#if isLoading}<p class="muted">デバイスを読み込み中...</p>{:else if devices.length}<section class="device-grid">{#each devices as device}<article class:locked={device.isLocked === true} class="device-card"><div class="card-top"><span class:online={isOnline(device.status)} class="status-dot"></span><span class="status">{statusLabel(device.status)}</span></div><a href={`/devices/${device.id}`}><h2>{device.name}</h2></a><div class="lock-state small">{lockLabel(device.isLocked)}</div><p class="muted">バッテリー {device.batteryLevel == null ? '-' : `${device.batteryLevel}%`}　Wi‑Fi {device.wifiRssi == null ? '-' : `${device.wifiRssi} dBm`}</p><div class="actions"><button class="success-button" disabled={busyDeviceId === device.id || !isOnline(device.status)} onclick={() => operate(device.id, 'unlock')}>解錠</button><button class="danger-button" disabled={busyDeviceId === device.id || !isOnline(device.status)} onclick={() => operate(device.id, 'lock')}>施錠</button></div></article>{/each}</section>{:else}<section class="empty panel"><h2>デバイスがありません</h2><p>ESP32をセットアップして、スマートロックを追加しましょう。</p><a class="button primary" href="/setup/provision">デバイスを追加</a></section>{/if}
      {/if}
    </main>
  </div>
{/if}

<style>
  :global(*) { box-sizing: border-box; }
  :global(body) { margin: 0; background: #f5f7fb; color: #172033; font-family: Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif; }
  :global(a) { color: inherit; text-decoration: none; }
  .auth-shell { min-height: 100vh; display: grid; place-items: center; padding: 24px; background: radial-gradient(circle at top right, #dce9ff, transparent 45%), #f5f7fb; }
  .auth-card { width: min(100%, 440px); padding: 36px; border-radius: 22px; background: white; box-shadow: 0 18px 55px #263b6318; }
  .auth-card.center { text-align: center; }
  .brand { color: #2257d6; font-size: 1.25rem; font-weight: 800; }
  h1, h2, p { margin-top: 0; } h1 { margin-bottom: 28px; font-size: clamp(1.8rem, 4vw, 2.6rem); } h2 { margin-bottom: 8px; font-size: 1.1rem; }
  .muted { color: #69758c; } .eyebrow { margin-bottom: 6px; color: #6b7aa0; font-size: .78rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
  form { display: grid; gap: 16px; } label { display: grid; gap: 7px; color: #35415a; font-size: .9rem; font-weight: 650; } input { width: 100%; padding: 12px 13px; border: 1px solid #d8dfeb; border-radius: 10px; font: inherit; } input:focus { outline: 3px solid #2257d622; border-color: #2257d6; }
  button, .button { border: 0; border-radius: 10px; padding: 11px 15px; font: inherit; font-weight: 700; cursor: pointer; display: inline-flex; justify-content: center; align-items: center; gap: 6px; } button:disabled { cursor: wait; opacity: .55; } .primary { color: white; background: #2257d6; } .secondary { color: #33415e; background: #edf1f7; } .success-button { color: #11633e; background: #d9f5e7; } .danger-button { color: #a33a4a; background: #fde2e5; }
  .switch { margin: 20px 0 0; text-align: center; font-size: .9rem; } .switch a, .back, .panel-heading a { color: #2257d6; font-weight: 700; }
  .alert { margin-bottom: 20px; padding: 13px 15px; border-radius: 10px; } .alert.error { color: #8d2637; background: #fde7ea; } .alert.success { color: #14623f; background: #def7e9; }
  .app-shell { min-height: 100vh; } .topbar { min-height: 66px; display: flex; align-items: center; gap: 28px; padding: 0 5vw; background: #fff; border-bottom: 1px solid #e6ebf3; } .topbar nav { display: flex; gap: 20px; flex: 1; } .topbar nav a { padding: 23px 0 20px; color: #6b758b; border-bottom: 3px solid transparent; } .topbar nav a.active { color: #2257d6; border-color: #2257d6; } .account { display: flex; align-items: center; gap: 12px; color: #69758c; font-size: .9rem; } .account button { padding: 7px 10px; color: #53617b; background: transparent; }
  .content { width: min(1180px, 90vw); margin: 0 auto; padding: 48px 0 80px; } .page-heading { display: flex; justify-content: space-between; align-items: center; gap: 20px; margin-bottom: 30px; } .page-heading h1 { margin-bottom: 0; }
  .device-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 18px; } .device-card, .panel, .device-row { background: white; border: 1px solid #e8edf5; border-radius: 16px; box-shadow: 0 7px 24px #263b6308; } .device-card { padding: 22px; border-top: 4px solid #38b985; } .device-card.locked { border-top-color: #e06474; } .card-top { display: flex; justify-content: space-between; margin-bottom: 26px; } .status-dot { width: 10px; height: 10px; margin-top: 5px; border-radius: 50%; background: #aeb7c8; } .status-dot.online { background: #28ad76; box-shadow: 0 0 0 4px #28ad7624; } .status { color: #7a8599; font-size: .8rem; font-weight: 700; } .status.online { color: #168458; } .lock-state { margin: 18px 0; font-size: 1.35rem; font-weight: 800; } .lock-state.small { margin: 14px 0; font-size: 1rem; } .device-card .muted { min-height: 24px; font-size: .85rem; } .actions { display: flex; flex-wrap: wrap; gap: 9px; margin-top: 18px; } .actions button { flex: 1; }
  .device-list { display: grid; gap: 12px; } .device-row { display: flex; align-items: center; gap: 16px; padding: 17px 20px; } .device-icon { width: 42px; font-size: 1.5rem; text-align: center; } .device-main { flex: 1; } .device-main h2 { margin: 0; } .device-main p, .device-meta { margin: 4px 0 0; color: #78849a; font-size: .85rem; } .device-meta { display: flex; gap: 15px; }
  .detail-grid { display: grid; grid-template-columns: minmax(0, .9fr) minmax(0, 1.1fr); gap: 20px; } .panel { padding: 24px; } .state-panel { min-height: 340px; } .panel-heading { display: flex; justify-content: space-between; align-items: center; } .panel-heading h2 { margin: 0; } dl { margin: 28px 0 0; } dl div { display: flex; justify-content: space-between; padding: 12px 0; border-top: 1px solid #edf0f5; } dt { color: #748097; } dd { margin: 0; font-weight: 700; } .log-list { margin-top: 18px; } .log-row { display: grid; grid-template-columns: 1.5fr 1fr .6fr; gap: 10px; padding: 12px 0; border-bottom: 1px solid #edf0f5; font-size: .85rem; } .log-row.failed { color: #b03c4b; } .empty { padding: 60px 24px; text-align: center; }
  .provision-panel { max-width: 820px; margin: 0 auto; } .provision-panel > h2 { margin-top: 28px; } .provision-panel label { max-width: 620px; margin-top: 16px; } .provision-panel > button { margin-top: 24px; } .stepper { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 7px; margin-bottom: 26px; } .stepper span { padding: 10px 5px; color: #8b95a8; background: #f0f3f8; border-radius: 8px; text-align: center; font-size: .75rem; font-weight: 700; } .stepper span.active { color: white; background: #2257d6; } .stepper span.completed { color: #14623f; background: #def7e9; } .mono { max-width: 70%; overflow-wrap: anywhere; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: .85rem; text-align: right; }
  @media (max-width: 760px) { .topbar { flex-wrap: wrap; gap: 8px 18px; padding: 13px 5vw 0; } .topbar nav { order: 3; flex-basis: 100%; gap: 15px; overflow-x: auto; } .topbar nav a { padding: 8px 0 10px; white-space: nowrap; } .account { margin-left: auto; } .content { padding-top: 30px; } .detail-grid { grid-template-columns: 1fr; } .device-row { flex-wrap: wrap; } .device-meta { order: 4; width: 100%; } .device-row > .button { margin-left: auto; } .stepper { grid-template-columns: repeat(3, 1fr); } .mono { max-width: 58%; } }
</style>
