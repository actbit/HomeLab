# 🔒 HomeLab - SESAME スマートロック制御システム

ESP32 経由で SESAME スマートロックを制御し、Web/ネイティブアプリで管理する IoT プラットフォーム。

ASP.NET Core 10 サーバーに WebSocket・SignalR を内蔵し、外部 MQTT ブローカー不要で動作します。

## 🏗 アーキテクチャ

```
┌──────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  Web Client  │  │  Native Client  │  │     ESP32       │
│ (Blazor WASM)│  │  (Avalonia 12)  │  │  (PlatformIO)   │
│  + Web BLE   │  │ Android/Windows │  │  BLE → SESAME   │
└──────┬───────┘  └───────┬─────────┘  └────────┬────────┘
       │                  │                      │
       │  REST API (JWT)  │                      │ WebSocket
       └────────┬─────────┘                      │ (JSON)
                │                                │
                ▼                                │
     ┌──────────────────────────┐                │
     │   ASP.NET Core 10 API   │◄───────────────┘
     │  + Blazor Server UI      │
     │  + WebSocket (内蔵)       │    ← 外部Broker不要
     │  + SignalR (Push通知)     │
     │  + Finbuckle MultiTenant │
     │  + FIDO2 Passkey Auth    │
     └──────────┬───────────────┘
                │
     ┌──────────┴──────────┐
     │                     │
┌────▼─────┐        ┌─────▼──────┐
│PostgreSQL│        │   SQLite   │
│ (本番用)  │        │(開発/軽量用)│
└──────────┘        └────────────┘
```

### ロック操作フロー

```
[Client] POST /api/locks/{id}/action (lock/unlock)
  → [Server] DeviceConnectionService が WebSocket で ESP32 にコマンド送信
    → [ESP32] SESAME に BLE 接続 → 施錠/解錠
      → [ESP32] WebSocket で結果をサーバーに通知
        → [Server] DB 更新 + SignalR でブラウザにリアルタイム通知
```

## ✨ 機能一覧

### サーバー (ASP.NET Core 10)

| 機能 | 詳細 |
|------|------|
| **認証** | JWT + Cookie / Passkey (FIDO2) / TOTP (Google Authenticator) |
| **デバイス管理** | CRUD、ステータス監視、バッテリー/WiFi強度表示 |
| **ロック制御** | 施錠/解錠/トグル、操作ログ記録 |
| **リアルタイム通信** | WebSocket (ESP32↔サーバー) / SignalR (サーバー↔ブラウザ) |
| **マルチテナンシー** | Finbuckle (ユーザー単位でデータ分離) |
| **DB 切り替え** | PostgreSQL (本番) / SQLite (開発) |

### ESP32 ファームウェア

| 機能 | 詳細 |
|------|------|
| **WebSocket 接続** | サーバーに直接接続 (MQTT Broker 不要) |
| **SESAME BLE 制御** | libsesame3bt による直接 BLE 操作 |
| **BLE プロビジョニング** | 初回設定時、Web BLE 経由で WiFi/SESAME 情報を書き込み |
| **自動再接続** | WiFi・WebSocket 断絶時に自動復旧 |
| **ハートビート** | 30秒間隔でロック状態・バッテリー・WiFi強度を報告 |

### ネイティブクライアント (Avalonia 12)

| プラットフォーム | 状態 |
|-----------------|------|
| **Windows Desktop** | ✅ ビルド可能 |
| **Android** | ✅ ビルド可能 |

## 📁 プロジェクト構成

```
HomeLab/
├── HomeLab.slnx                           # ソリューションファイル
├── HomeLab.AppHost/                       # .NET Aspire ホスト
├── HomeLab.ServiceDefaults/               # 共通サービス設定
├── HomeLab.Shared/                        # 共通モデル (DTO, Enum, MQTT定義)
│   ├── DTOs/                              #   リクエスト/レスポンス DTO
│   └── Enums/                             #   DeviceStatus, LockActionType
├── HomeLab/
│   ├── HomeLab/                           # ASP.NET Core 10 サーバー
│   │   ├── API/                           #   Minimal API エンドポイント
│   │   │   ├── AuthEndpoints.cs           #     /api/auth/*
│   │   │   ├── DeviceEndpoints.cs         #     /api/devices/*
│   │   │   └── LockEndpoints.cs           #     /api/locks/*
│   │   ├── Authentication/                #   認証
│   │   │   ├── Passkey/                   #     FIDO2/WebAuthn
│   │   │   └── Totp/                      #     TOTP (Google Authenticator)
│   │   ├── Data/                          #   EF Core DbContext
│   │   ├── Hubs/                          #   SignalR Hub
│   │   ├── Middleware/                    #   WebSocket エンドポイント
│   │   ├── Models/Entities/               #   エンティティモデル
│   │   │   ├── AppUser.cs                 #     ユーザー (Identity拡張)
│   │   │   ├── PasskeyCredential.cs       #     パスキー
│   │   │   ├── LockDevice.cs              #     ロックデバイス
│   │   │   └── LockLog.cs                 #     操作ログ
│   │   ├── Services/
│   │   │   └── DeviceConnectionService.cs #     WebSocket 接続管理
│   │   ├── Tenancy/                       #   Finbuckle マルチテナンシー
│   │   └── Components/                    #   Blazor UI
│   ├── HomeLab.Client/                    # Blazor WASM クライアント
│   └── HomeLab.Tests/
│       └── HomeLab.Server.Tests/          # 統合テスト (xUnit)
├── HomeLab.NativeClient/
│   ├── HomeLab.NativeClient/              # Avalonia 共通コア
│   ├── HomeLab.NativeClient.Android/      # Android
│   └── HomeLab.NativeClient.Desktop/      # Windows Desktop
└── HomeLab.Esp32/                         # ESP32 ファームウェア (PlatformIO)
    ├── platformio.ini
    └── src/
        ├── main.cpp                       #   エントリーポイント
        ├── config.h                       #   NVS 設定管理
        ├── websocket_client.h             #   WebSocket クライアント
        ├── sesame_ble.h                   #   SESAME BLE 制御
        ├── ble_provisioning.h             #   BLE GATT Server
        ├── wifi_manager.h                 #   WiFi 接続管理
        └── crypto.h                       #   ECDSA 鍵生成・署名
```

## 🛠 技術スタック

### サーバー

| 技術 | バージョン | 用途 |
|------|-----------|------|
| .NET | 10 | ランタイム |
| ASP.NET Core | 10 | Web API / Blazor |
| Entity Framework Core | 10 | ORM |
| Finbuckle.MultiTenant | 10.1 | マルチテナンシー |
| Fido2 | 4.0 | WebAuthn / Passkey |
| Otp.NET | 1.4 | TOTP 認証 |
| QRCoder | 1.8 | QR コード生成 |
| SignalR | - | リアルタイム Push 通知 |
| PostgreSQL / SQLite | - | データベース |

### ESP32

| ライブラリ | 用途 |
|-----------|------|
| arduinoWebSockets | WebSocket クライアント |
| libsesame3bt | SESAME BLE 制御 |
| NimBLE | BLE スタック |
| ArduinoJson | JSON 処理 |
| micro-ecc | ECDSA 署名 |

### ネイティブクライアント

| 技術 | 用途 |
|------|------|
| Avalonia 12 | クロスプラットフォーム UI |
| CommunityToolkit.Mvvm | MVVM バインディング |

## 🚀 セットアップ

### 前提条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PlatformIO](https://platformio.org/) (ESP32 開発用)
- ESP32 開発ボード
- SESAME スマートロック (BLE 対応モデル)

### 1. サーバー起動

```bash
cd HomeLab/HomeLab/HomeLab

# 開発環境 (SQLite)
dotnet run

# 本番環境 (PostgreSQL)
# appsettings.json で Database:Provider = "PostgreSQL" に設定
dotnet run --environment Production
```

#### 設定 (`appsettings.json`)

```json
{
  "Database": {
    "Provider": "SQLite",
    "ConnectionString": {
      "SQLite": "Data Source=homelab.db",
      "PostgreSQL": "Host=localhost;Database=homelab;Username=user;Password=pass"
    }
  },
  "Jwt": {
    "Issuer": "HomeLab",
    "Audience": "HomeLab",
    "SecretKey": "your-secret-key-at-least-32-characters-long",
    "AccessTokenExpirationMinutes": 15
  },
  "Passkey": {
    "Origin": "https://your-domain.com",
    "RelyingPartyName": "HomeLab SESAME",
    "RelyingPartyId": "your-domain.com"
  }
}
```

### 2. ESP32 ファームウェア書き込み

```bash
cd HomeLab.Esp32

# ビルド
pio run

# ESP32 に書き込み + シリアルモニタ
pio run --target upload && pio device monitor
```

#### 初回プロビジョニングフロー

```
1. ESP32 起動 → BLE GATT Server として待機
2. ブラウザ (Web BLE) から ESP32 に BLE 接続
3. WiFi SSID/Password を ESP32 に送信
4. SESAME デバイス UUID/API Key を ESP32 に送信
5. サーバーにデバイス登録 → アクティベーションキー取得
6. アクティベーションキーを ESP32 に送信
7. ESP32 再起動 → WiFi + WebSocket 接続 → 運用開始
```

### 3. ネイティブクライアント

```bash
cd HomeLab.NativeClient

# Windows Desktop
dotnet build HomeLab.NativeClient.Desktop/HomeLab.NativeClient.Desktop.csproj

# Android
dotnet build HomeLab.NativeClient.Android/HomeLab.NativeClient.Android.csproj
```

## 📡 API エンドポイント

### 認証 (`/api/auth`)

| メソッド | パス | 認証 | 説明 |
|---------|------|------|------|
| POST | `/login` | - | メール/パスワード ログイン |
| POST | `/register` | - | アカウント登録 |
| POST | `/passkey/register-options` | - | Passkey 登録オプション |
| POST | `/passkey/register` | ✅ | Passkey 登録完了 |
| POST | `/passkey/login-options` | - | Passkey ログインオプション |
| POST | `/passkey/login` | - | Passkey ログイン |
| POST | `/totp/setup` | ✅ | TOTP QR コード生成 |
| POST | `/totp/enable` | ✅ | TOTP 有効化 |
| POST | `/totp/disable` | ✅ | TOTP 無効化 |
| GET | `/passkeys` | ✅ | 登録済みパスキー一覧 |
| DELETE | `/passkeys/{id}` | ✅ | パスキー削除 |

### デバイス (`/api/devices`)

| メソッド | パス | 認証 | 説明 |
|---------|------|------|------|
| GET | `/` | ✅ | デバイス一覧 |
| GET | `/{id}` | ✅ | デバイス詳細 |
| POST | `/register` | ✅ | デバイス登録 (プロビジョニング) |
| DELETE | `/{id}` | ✅ | デバイス削除 |
| GET | `/{id}/logs` | ✅ | 操作ログ取得 (ページング対応) |

### ロック操作 (`/api/locks`)

| メソッド | パス | 認証 | 説明 |
|---------|------|------|------|
| POST | `/{id}/action` | ✅ | 施錠/解錠/トグル |
| GET | `/{id}/status` | ✅ | ロック状態取得 |

### WebSocket

| パス | 説明 |
|------|------|
| `/ws/device?deviceId=xxx&activationKey=yyy` | ESP32 デバイス接続 |

### SignalR Hub

| パス | 説明 |
|------|------|
| `/hubs/lock` | ロック状態変更通知 |

## 🧪 テスト

```bash
cd HomeLab/HomeLab/HomeLab.Tests/HomeLab.Server.Tests

# 全テスト実行
dotnet test

# 詳細出力
dotnet test --verbosity normal
```

### テスト概要 (39 テスト)

| テストクラス | 件数 | 内容 |
|-------------|------|------|
| `AuthEndpointsTests` | 7 | 登録/ログイン/パスワード/認証エラー |
| `DeviceEndpointsTests` | 7 | CRUD/他ユーザー分離 |
| `LockEndpointsTests` | 7 | 施錠/解錠/オフライン/権限 |
| `TotpServiceTests` | 10 | 秘密鍵生成/QR/検証/リカバリー |
| `MqttTopicsTests` | 8 | トピック/メッセージフォーマット |

テストは `WebApplicationFactory` + EF Core InMemory を使用し、各テストクラスが独立した DB インスタンスを持ちます。

## 📊 データモデル

```
AppUser (IdentityUser)
├── DisplayName, TotpSecretKey, TotpEnabled, CreatedAt
├── PasskeyCredential[]
│   ├── CredentialId, PublicKey, UserHandle
│   ├── SignatureCounter, AuthenticatorType, Aaguid
│   └── Name, CreatedAt, LastUsedAt
└── LockDevice[]
    ├── Name, DevicePublicKey, DeviceIdentifier
    ├── ActivationKey, SesameDeviceUuid, SesameApiKey
    ├── Status, IsLocked, BatteryLevel, WifiRssi
    ├── LastSeenAt, CreatedAt
    └── LockLog[]
        ├── ActionType, TriggeredBy, UserId
        ├── RequestId, Success, ErrorMessage
        └── Timestamp
```

## 🔐 セキュリティ

- **JWT 認証**: HMAC-SHA256 署名付きトークン
- **Passkey (FIDO2)**: パスワードレス認証、フィッシング耐性
- **TOTP**: 二要素認証 (Google Authenticator 互換)
- **ECDSA P-256**: ESP32 デバイス署名 (チャレンジ・レスポンス)
- **マルチテナンシー**: ユーザー間データ分離

## 📄 ライセンス

このプロジェクトは [LICENSE.txt](LICENSE.txt) のもとで公開されています。
