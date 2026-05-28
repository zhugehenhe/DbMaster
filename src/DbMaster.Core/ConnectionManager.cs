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
    /// 若别名已存在，先释放旧连接再建立新连接。
    /// </summary>
    /// <param name="dbType">null/"auto"=自动检测, "sqlite"|"mysql"|"postgresql"|"sqlserver"</param>
    /// <returns>检测到的数据库类型</returns>
    public async Task<string> ConnectAsync(string alias, string connectionString, string? dbType = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        if (_connections.Count >= MaxConnections && !_connections.ContainsKey(alias))
            throw new InvalidOperationException(
                $"Connection limit reached ({MaxConnections}). Disconnect unused aliases first.");

        var adapter = AdapterFactory.Create(connectionString, dbType);
        await adapter.TestConnectionAsync(ct);

        // 如果别名已存在，先释放旧连接（修复 #2：资源泄漏）
        if (_connections.TryGetValue(alias, out var oldEntry))
        {
            oldEntry.Adapter.Dispose();
        }

        _connections[alias] = new ConnectionEntry(adapter, DateTime.UtcNow);
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

        // 修复 #3：超时时直接 TryRemove + Dispose，避免竞态
        if (DateTime.UtcNow - entry.LastAccess > IdleTimeout)
        {
            if (_connections.TryRemove(alias, out entry))
            {
                entry.Adapter.Dispose();
            }
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

    private sealed class ConnectionEntry(IDbAdapter adapter, DateTime connectedAt)
    {
        public IDbAdapter Adapter { get; } = adapter;
        public DateTime ConnectedAt { get; } = connectedAt;
        public DateTime LastAccess { get; set; } = connectedAt;
    }
}
