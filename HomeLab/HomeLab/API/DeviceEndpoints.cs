using System.Security.Claims;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Server.Services;
using HomeLab.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.API;

/// <summary>
/// デバイス管理のMinimal APIエンドポイント
/// </summary>
public static class DeviceEndpoints
{
    public static WebApplication MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .RequireAuthorization();

        // ===== デバイス一覧取得 =====
        group.MapGet("/", async (
            AppDbContext dbContext,
            DeviceConnectionService connections,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var devices = await dbContext.LockDevices
                .Where(d => d.UserId == userId)
                .OrderBy(d => d.Name)
                .ToListAsync();

            return Results.Ok(devices.Select(d => new DeviceListItem(
                d.Id, d.Name, Enum.Parse<Shared.Enums.DeviceStatus>(d.Status),
                connections.GetKnownLockState(d.Id), d.LastSeenAt, d.BatteryLevel, d.WifiRssi)));
        });

        // ===== デバイス詳細取得 =====
        group.MapGet("/{id:guid}", async (
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

            return Results.Ok(new DeviceDetail(
                device.Id,
                device.Name,
                Enum.Parse<Shared.Enums.DeviceStatus>(device.Status),
                connections.GetKnownLockState(device.Id),
                device.SesameDeviceUuid,
                device.LastSeenAt,
                device.CreatedAt,
                device.BatteryLevel,
                device.WifiRssi));
        });

        // ===== デバイス登録 (プロビジョニング時) =====
        group.MapPost("/register", async (
            [FromBody] DeviceRegisterRequest request,
            AppDbContext dbContext,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            // 同一デバイス識別子の重複チェック
            var exists = await dbContext.LockDevices
                .AnyAsync(d => d.DeviceIdentifier == request.DeviceId);

            if (exists)
            {
                return Results.BadRequest(new { error = "Device already registered" });
            }

            // アクティベーションキー生成
            var activationKey = Guid.NewGuid().ToString("N");

            var device = new LockDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = request.Name,
                DevicePublicKey = request.DevicePublicKey,
                DeviceIdentifier = request.DeviceId,
                SesameDeviceUuid = request.SesameDeviceUuid,
                // SESAME鍵はBLEでESP32へ送るため、サーバーに保存する必要はない。
                SesameApiKey = null,
                ActivationKey = DeviceConnectionService.HashActivationKey(activationKey),
                Status = "Offline",
                IsLocked = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            dbContext.LockDevices.Add(device);
            await dbContext.SaveChangesAsync();

            // ESP32がそのままWebSocketへ接続できる完全なURLを返す。
            // 旧実装はホスト名だけを返していたため、ESP32側のURL解析に失敗していた。
            var requestScheme = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                ?? httpContext.Request.Scheme;
            var websocketScheme = requestScheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";
            var serverUrl = $"{websocketScheme}://{httpContext.Request.Host}/ws/device";

            return Results.Ok(new DeviceRegisterResponse(
                device.Id,
                activationKey,
                serverUrl));
        });

        // ===== デバイス削除 =====
        group.MapDelete("/{id:guid}", async (
            Guid id,
            AppDbContext dbContext,
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

            dbContext.LockDevices.Remove(device);
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { success = true });
        });

        // ===== デバイス操作ログ取得 =====
        group.MapGet("/{id:guid}/logs", async (
            Guid id,
            AppDbContext dbContext,
            HttpContext httpContext,
            int page = 1,
            int pageSize = 20) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var device = await dbContext.LockDevices
                .AnyAsync(d => d.Id == id && d.UserId == userId);

            if (!device)
            {
                return Results.NotFound();
            }

            if (page < 1 || pageSize is < 1 or > 100)
                return Results.BadRequest(new { error = "Invalid pagination" });

            var logs = await dbContext.LockLogs
                .Where(l => l.DeviceId == id)
                .ToListAsync();
            var result = logs
                .OrderByDescending(l => l.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new LockLogEntry(
                    l.Id,
                    Enum.Parse<Shared.Enums.LockActionType>(l.ActionType),
                    l.TriggeredBy,
                    l.UserId,
                    l.Success,
                    l.ErrorMessage,
                    l.Timestamp))
                .ToList();

            return Results.Ok(result);
        });

        return app;
    }
}
