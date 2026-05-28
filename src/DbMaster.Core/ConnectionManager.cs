using System.Collections.Concurrent;

namespace DbMaster.Core;

/// <summary>
/// 连接管理器 — 管理多个数据库连接的生命周期。
/// 线程安全，支持别名寻址、空闲超时断开、连接数上限。
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();

    /// <summary>最大并发连接数，默认 10</summary>
    public int MaxConnections { get; init; } = 10;

    /// <summary>空闲超时，默认 30 分钟</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 建立新连接（或替换已有别名）。
    /// </summary>
    /// <returns>检测到的数据库类型</returns>
    public async Task<string> ConnectAsync(string alias, string connectionString, CancellationToken ct = default)
    {
        if (_connections.Count >= MaxConnections && !_connections.ContainsKey(alias))
            throw new InvalidOperationException(
                $"Connection limit reached ({MaxConnections}). Disconnect unused aliases first.");

        var adapter = AdapterFactory.Create(connectionString);
        await adapter.TestConnectionAsync(ct);

        _connections[alias] = new ConnectionEntry(adapter, connectionString, DateTime.UtcNow);
        return adapter.DbType;
    }

    /// <summary>断开并释放指定连接</summary>
    public bool Disconnect(string alias)
    {
        if (_connections.TryRemove(alias, out var entry))
        {
            entry.Adapter.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 获取适配器（自动检查空闲超时）。
    /// 返回 null 表示连接不存在或已超时断开。
    /// </summary>
    public IDbAdapter? GetAdapter(string alias)
    {
        if (!_connections.TryGetValue(alias, out var entry))
            return null;

        if (DateTime.UtcNow - entry.LastAccess > IdleTimeout)
        {
            Disconnect(alias);
            return null;
        }

        entry.LastAccess = DateTime.UtcNow;
        return entry.Adapter;
    }

    /// <summary>列出所有活动连接（不含连接字符串）</summary>
    public IReadOnlyList<ConnectionInfo> ListConnections()
        => _connections.Select(kv => new ConnectionInfo
        {
            Alias = kv.Key,
            DbType = kv.Value.Adapter.DbType,
            ConnectedAt = kv.Value.ConnectedAt,
            LastAccess = kv.Value.LastAccess,
        }).ToList();

    public void Dispose()
    {
        foreach (var entry in _connections.Values)
            entry.Adapter.Dispose();
        _connections.Clear();
    }

    private sealed class ConnectionEntry(IDbAdapter adapter, string connStr, DateTime connectedAt)
    {
        public IDbAdapter Adapter { get; } = adapter;
        public string ConnectionString { get; } = connStr;
        public DateTime ConnectedAt { get; } = connectedAt;
        public DateTime LastAccess { get; set; } = connectedAt;
    }
}
