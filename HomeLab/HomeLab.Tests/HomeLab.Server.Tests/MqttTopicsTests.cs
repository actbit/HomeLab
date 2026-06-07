using HomeLab.Shared.MQTT;

namespace HomeLab.Server.Tests;

public class MqttTopicsTests
{
    [Fact]
    public void Command_ReturnsCorrectFormat()
    {
        // Act
        var topic = MqttTopics.Command("user123", "device456");

        // Assert
        Assert.Equal("homelab/user123/device456/command", topic);
    }

    [Fact]
    public void Status_ReturnsCorrectFormat()
    {
        // Act
        var topic = MqttTopics.Status("user123", "device456");

        // Assert
        Assert.Equal("homelab/user123/device456/status", topic);
    }

    [Fact]
    public void Heartbeat_ReturnsCorrectFormat()
    {
        // Act
        var topic = MqttTopics.Heartbeat("user123", "device456");

        // Assert
        Assert.Equal("homelab/user123/device456/heartbeat", topic);
    }

    [Fact]
    public void TenantWildcard_ReturnsCorrectFormat()
    {
        // Act
        var topic = MqttTopics.TenantWildcard("user123");

        // Assert
        Assert.Equal("homelab/user123/#", topic);
    }

    [Fact]
    public void Prefix_IsHomelab()
    {
        Assert.Equal("homelab", MqttTopics.Prefix);
    }
}

public class MqttMessageTests
{
    [Fact]
    public void MqttCommandMessage_RecordEquality()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg1 = new MqttCommandMessage("unlock", "req123", now);
        var msg2 = new MqttCommandMessage("unlock", "req123", now);

        // Assert
        Assert.Equal(msg1, msg2);
    }

    [Fact]
    public void MqttStatusMessage_WithOptionalFields()
    {
        // Arrange & Act
        var msg = new MqttStatusMessage(
            "locked", "req123", DateTimeOffset.UtcNow,
            Success: true, ErrorMessage: null);

        // Assert
        Assert.Equal("locked", msg.State);
        Assert.True(msg.Success);
        Assert.Null(msg.ErrorMessage);
    }

    [Fact]
    public void MqttHeartbeatMessage_RecordProperties()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var msg = new MqttHeartbeatMessage("dev1", true, 85, -42, now);

        // Assert
        Assert.Equal("dev1", msg.DeviceId);
        Assert.True(msg.IsLocked);
        Assert.Equal(85, msg.BatteryLevel);
        Assert.Equal(-42, msg.WifiRssi);
    }
}
