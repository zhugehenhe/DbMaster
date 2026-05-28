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

        // 修复 #1：先原子操作抢占槽位，再建连接
        var sentinel = new ConnectionEntry(new PendingAdapter(), DateTime.UtcNow);
        var existing = _connections.GetOrAdd(alias, sentinel);

        if (existing != sentinel)
        {
            // 别名已存在 → 需要替换。先检查上限（替换不占新槽位）
            if (_connections.Count > MaxConnections)
            {
                _connections.TryRemove(alias, out _);
                throw new InvalidOperationException(
                    $"Connection limit reached ({MaxConnections}). Disconnect unused aliases first.");
            }
            // 不检查上限，因为替换不增加连接数
        }
        else if (_connections.Count > MaxConnections)
        {
            _connections.TryRemove(alias, out _);
            throw new InvalidOperationException(
                $"Connection limit reached ({MaxConnections}). Disconnect unused aliases first.");
        }

        try
        {
            var adapter = AdapterFactory.Create(connectionString, dbType);
            await adapter.TestConnectionAsync(ct);

            // 替换 sentinel 为真实 adapter
            _connections[alias] = new ConnectionEntry(adapter, DateTime.UtcNow);
            return adapter.DbType;
        }
        catch
        {
            // 连接失败，清理占位
            _connections.TryRemove(alias, out _);
            throw;
        }
    }

    /// <summary>Pending adapter — 占位用，不可调用任何方法</summary>
    private sealed class PendingAdapter : IDbAdapter
    {
        public string DbType => "pending";
        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<QueryResult> QueryAsync(string s, int m, CancellationToken ct) => throw new InvalidOperationException();
        public Task<QueryResult> QueryAsync(string s, int m, int to, CancellationToken ct) => throw new InvalidOperationException();
        public Task<int> ExecuteAsync(string s, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<TableSchema> DescribeTableAsync(string t, CancellationToken ct) => throw new InvalidOperationException();
        public Task<QueryResult> ExplainAsync(string s, int m, CancellationToken ct) => throw new InvalidOperationException();
        public void Dispose() { }
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
