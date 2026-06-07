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
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var devices = await dbContext.LockDevices
                .Where(d => d.UserId == userId)
                .Select(d => new DeviceListItem(
                    d.Id,
                    d.Name,
                    Enum.Parse<Shared.Enums.DeviceStatus>(d.Status),
                    d.IsLocked,
                    d.LastSeenAt))
                .OrderBy(d => d.Name)
                .ToListAsync();

            return Results.Ok(devices);
        });

        // ===== デバイス詳細取得 =====
        group.MapGet("/{id:guid}", async (
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

            return Results.Ok(new DeviceDetail(
                device.Id,
                device.Name,
                Enum.Parse<Shared.Enums.DeviceStatus>(device.Status),
                device.IsLocked,
                device.SesameDeviceUuid,
                device.LastSeenAt,
                device.CreatedAt));
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
                SesameApiKey = request.SesameApiKey,
                ActivationKey = activationKey,
                Status = "Offline",
                IsLocked = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            dbContext.LockDevices.Add(device);
            await dbContext.SaveChangesAsync();

            var mqttBrokerUrl = httpContext.Request.Host.ToString();

            return Results.Ok(new DeviceRegisterResponse(
                device.Id,
                activationKey,
                mqttBrokerUrl));
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

            var logs = await dbContext.LockLogs
                .Where(l => l.DeviceId == id)
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
                .ToListAsync();

            return Results.Ok(logs);
        });

        return app;
    }
}
