using HomeLab.Server.Services;

namespace HomeLab.Server.Middleware;

/// <summary>
/// ESP32デバイス用WebSocketエンドポイント
/// ws://server/ws/device?deviceId=xxx&activationKey=yyy
/// </summary>
public static class DeviceWebSocketMiddleware
{
    public static WebApplication MapDeviceWebSocket(this WebApplication app)
    {
        app.Map("/ws/device", async (HttpContext context, DeviceConnectionService connectionService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // 最初の認証メッセージを待つ（deviceIdとactivationKeyはメッセージ内で送信）
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await connectionService.HandleConnectionAsync(ws, context.RequestAborted);
        });

        return app;
    }
}
