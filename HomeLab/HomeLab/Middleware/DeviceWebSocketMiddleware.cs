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

            var deviceId = context.Request.Query["deviceId"].ToString();
            var activationKey = context.Request.Query["activationKey"].ToString();

            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(activationKey))
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            await connectionService.HandleConnectionAsync(ws, deviceId, activationKey);
        });

        return app;
    }
}
