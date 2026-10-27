using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HomeLab.Server.Data;
using HomeLab.Shared.DTOs;
using HomeLab.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace HomeLab.Server.Tests;

public sealed class DeviceWebSocketTests
{
    [Fact]
    public async Task ReconnectingDeviceKeepsTheNewConnectionOnline()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, token) = await factory.CreateTestUserAsync("reconnect@test.com");
        var device = await factory.CreateTestDeviceAsync(userId, status: "Offline");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.LockDevices.FindAsync(device.Id))!.ActivationKey = "reconnect-test-key";
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var first = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/device"), CancellationToken.None);
        await Send(first, new { deviceId = device.DeviceIdentifier, activationKey = "reconnect-test-key" });
        await Send(first, new { type = "heartbeat", isLocked = true, stateConfirmed = true });
        await WaitForDevice(client, device.Id, value => value.IsLocked == true);

        using var second = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/device"), CancellationToken.None);
        await Send(second, new { deviceId = device.DeviceIdentifier, activationKey = "reconnect-test-key" });
        await Send(second, new { type = "heartbeat", isLocked = false, stateConfirmed = true });
        var reconnected = await WaitForDevice(client, device.Id, value => value.IsLocked == false);
        Assert.Equal(DeviceStatus.Online, reconnected.Status);
        var action = await client.PostAsJsonAsync($"/api/locks/{device.Id}/action", new LockActionRequest(LockActionType.Lock));
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        using var command = await Receive(second);
        Assert.Equal("lock", command.RootElement.GetProperty("action").GetString());
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await WaitForDevice(client, device.Id, value => value.Status == DeviceStatus.Offline);
    }

    [Fact]
    public async Task UnknownHeartbeatAndFailedCommandNeverBecomeUnlocked()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, token) = await factory.CreateTestUserAsync("socket@test.com");
        var device = await factory.CreateTestDeviceAsync(userId, status: "Offline");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.LockDevices.FindAsync(device.Id);
            stored!.ActivationKey = "socket-test-key";
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/device"), CancellationToken.None);
        await Send(socket, new { deviceId = device.DeviceIdentifier, activationKey = "socket-test-key" });
        await Send(socket, new { type = "heartbeat", isLocked = (bool?)null, batteryLevel = 80, wifiRssi = -50 });

        var initial = await WaitForDevice(client, device.Id, value => value.Status == DeviceStatus.Online);
        Assert.Null(initial.IsLocked);
        await Send(socket, new { type = "heartbeat", isLocked = true, stateConfirmed = true, batteryLevel = 80, wifiRssi = -50 });
        Assert.True((await WaitForDevice(client, device.Id, value => value.IsLocked == true)).IsLocked);

        var action = await client.PostAsJsonAsync($"/api/locks/{device.Id}/action", new LockActionRequest(LockActionType.Unlock));
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        var accepted = await action.Content.ReadFromJsonAsync<LockActionResponse>();
        Assert.NotNull(accepted?.RequestId);
        using var command = await Receive(socket);
        Assert.Equal(accepted.RequestId, command.RootElement.GetProperty("requestId").GetString());
        Assert.Null((await client.GetFromJsonAsync<DeviceDetail>($"/api/devices/{device.Id}"))!.IsLocked);

        await Send(socket, new { type = "status", state = "error", requestId = accepted.RequestId, success = false, errorMessage = "BLE failed" });
        await WaitUntil(async () => (await client.GetFromJsonAsync<List<LockLogEntry>>($"/api/devices/{device.Id}/logs"))!
            .Any(log => log.ErrorMessage == "BLE failed"));
        Assert.Null((await client.GetFromJsonAsync<DeviceDetail>($"/api/devices/{device.Id}"))!.IsLocked);

        var secondAction = await client.PostAsJsonAsync($"/api/locks/{device.Id}/action", new LockActionRequest(LockActionType.Unlock));
        var second = await secondAction.Content.ReadFromJsonAsync<LockActionResponse>();
        using var secondCommand = await Receive(socket);
        Assert.Equal(second!.RequestId, secondCommand.RootElement.GetProperty("requestId").GetString());
        await Send(socket, new { type = "status", state = "unlocked", stateConfirmed = true, requestId = second.RequestId, success = true });
        Assert.False((await WaitForDevice(client, device.Id, value => value.IsLocked == false)).IsLocked);
        await WaitUntil(async () => (await client.GetFromJsonAsync<List<LockLogEntry>>($"/api/devices/{device.Id}/logs"))!
            .Any(log => log.Success));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        var offline = await WaitForDevice(client, device.Id, value => value.Status == DeviceStatus.Offline);
        Assert.Null(offline.IsLocked);
    }

    private static async Task Send(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonDocument> Receive(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
    }

    private static async Task<DeviceDetail> WaitForDevice(HttpClient client, Guid id, Func<DeviceDetail, bool> condition)
    {
        DeviceDetail? result = null;
        await WaitUntil(async () => { result = await client.GetFromJsonAsync<DeviceDetail>($"/api/devices/{id}"); return result is not null && condition(result); });
        return result!;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("Timed out waiting for device state");
    }
}
