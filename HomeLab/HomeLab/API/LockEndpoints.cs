using System.Security.Claims;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Server.Services;
using HomeLab.Shared.DTOs;
using HomeLab.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.API;

/// <summary>
/// ロック操作のMinimal APIエンドポイント
/// コマンドはWebSocket経由でESP32に送信
/// </summary>
public static class LockEndpoints
{
    public static WebApplication MapLockEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/locks")
            .WithTags("Locks")
            .RequireAuthorization();

        // ===== ロック操作 (施錠/解錠/トグル) =====
        group.MapPost("/{id:guid}/action", async (
            Guid id,
            [FromBody] LockActionRequest request,
            AppDbContext dbContext,
            DeviceConnectionService connectionService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var device = await dbContext.LockDevices
                .FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);

            if (device is null)
            {
                return Results.NotFound();
            }

            if (device.Status != "Online")
            {
                return Results.BadRequest(new { error = "Device is offline" });
            }

            // WebSocket経由でコマンド送信
            var requestId = Guid.NewGuid().ToString("N")[..16];
            var action = request.Action.ToString().ToLowerInvariant();

            // デバイスからの応答が送信直後に届いても記録を更新できるよう先に保存する。
            var log = new LockLog
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                ActionType = request.Action.ToString(),
                TriggeredBy = "User",
                UserId = userId,
                RequestId = requestId,
                ErrorMessage = DeviceConnectionService.PendingCommand,
                Timestamp = DateTimeOffset.UtcNow,
            };

            dbContext.LockLogs.Add(log);
            await dbContext.SaveChangesAsync();

            var sent = await connectionService.SendCommandAsync(device.Id, action, requestId);
            if (!sent)
            {
                log.ErrorMessage = "Device not connected";
                await dbContext.SaveChangesAsync();
                return Results.BadRequest(new { error = "Device not connected" });
            }

            return Results.Ok(new LockActionResponse(true, requestId, null));
        });

        // ===== ロック状態取得 =====
        group.MapGet("/{id:guid}/status", async (
            Guid id,
            AppDbContext dbContext,
            DeviceConnectionService connections,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var device = await dbContext.LockDevices
                .FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);

            if (device is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                device.Id,
                device.Status,
                IsLocked = connections.GetKnownLockState(device.Id),
                device.BatteryLevel,
                device.WifiRssi,
                device.LastSeenAt,
            });
        });

        return app;
    }
}
