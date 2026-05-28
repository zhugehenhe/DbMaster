using DbMaster.Core;
using Xunit;

namespace DbMaster.Tests;

/// <summary>
/// ConnectionManager 单元测试。
/// </summary>
[Collection("AdapterRegistration")]
public class ConnectionManagerTests
{
    [Fact]
    public void ListConnections_InitiallyEmpty()
    {
        using var cm = new ConnectionManager();
        var list = cm.ListConnections();
        Assert.Empty(list);
    }

    [Fact]
    public void Disconnect_NonexistentAlias_ReturnsFalse()
    {
        using var cm = new ConnectionManager();
        Assert.False(cm.Disconnect("nonexistent"));
    }

    [Fact]
    public async Task ConnectAsync_ExceedMaxConnections_Throws()
    {
        using var cm = new ConnectionManager { MaxConnections = 1 };
        await cm.ConnectAsync("a", "Data Source=:memory:", "sqlite");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cm.ConnectAsync("b", "Data Source=:memory:", "sqlite"));
    }

    [Fact]
    public async Task ConnectAsync_ReplaceAlias_DisposesOldConnection()
    {
        using var cm = new ConnectionManager();
        await cm.ConnectAsync("test", "Data Source=:memory:", "sqlite");
        var first = cm.GetAdapter("test");
        Assert.NotNull(first);

        // Replace same alias
        await cm.ConnectAsync("test", "Data Source=:memory:", "sqlite");
        var second = cm.GetAdapter("test");
        Assert.NotNull(second);
        Assert.NotSame(first, second); // New adapter instance
    }

    [Fact]
    public async Task ConnectAsync_ReturnsDbType()
    {
        using var cm = new ConnectionManager();
        var type = await cm.ConnectAsync("db", "Data Source=:memory:", "sqlite");
        Assert.Equal("sqlite", type);
    }

    [Fact]
    public async Task GetAdapter_AfterDisconnect_ReturnsNull()
    {
        using var cm = new ConnectionManager();
        await cm.ConnectAsync("x", "Data Source=:memory:", "sqlite");
        cm.Disconnect("x");
        Assert.Null(cm.GetAdapter("x"));
    }

    [Fact]
    public async Task GetAdapter_Timeout_ReturnsNull()
    {
        using var cm = new ConnectionManager { IdleTimeout = TimeSpan.Zero };
        await cm.ConnectAsync("y", "Data Source=:memory:", "sqlite");
        Assert.Null(cm.GetAdapter("y")); // Immediate timeout
    }

    [Fact]
    public async Task ListConnections_ShowsActiveConnections()
    {
        using var cm = new ConnectionManager();
        await cm.ConnectAsync("alpha", "Data Source=:memory:", "sqlite");
        await cm.ConnectAsync("beta", "Data Source=:memory:", "sqlite");

        var list = cm.ListConnections();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.Alias == "alpha");
        Assert.Contains(list, c => c.Alias == "beta");
    }
}
