using System.Collections.Concurrent;
using Renci.SshNet;

namespace DbMaster.Core;

/// <summary>
/// SSH 隧道管理器 — 管理到远程服务器的端口转发隧道。
/// 使用 SSH.NET 库建立和维护 SSH 连接及端口转发。
/// </summary>
public sealed class SshTunnelManager : IDisposable
{
    private readonly ConcurrentDictionary<int, TunnelEntry> _tunnels = new();
    private int _nextPort = 13000;

    /// <summary>建立 SSH 隧道（本地端口 → 远程地址:端口），支持密码或密钥认证</summary>
    /// <returns>分配的本地端口号</returns>
    public async Task<int> CreateTunnelAsync(
        string sshHost, int sshPort, string sshUser, string sshPassword,
        string remoteHost, int remotePort,
        string? sshPrivateKey = null, string? sshPrivateKeyPassphrase = null,
        CancellationToken ct = default)
    {
        var localPort = Interlocked.Increment(ref _nextPort);

        SshClient client;

        if (!string.IsNullOrEmpty(sshPrivateKey))
        {
            // 密钥认证 — 支持文件路径或 PEM 内容
            if (File.Exists(sshPrivateKey))
            {
                var key = string.IsNullOrEmpty(sshPrivateKeyPassphrase)
                    ? new PrivateKeyFile(sshPrivateKey)
                    : new PrivateKeyFile(sshPrivateKey, sshPrivateKeyPassphrase);
                client = new SshClient(sshHost, sshPort, sshUser, key);
            }
            else
            {
                // 当作 PEM 内容处理
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sshPrivateKey));
                var key = string.IsNullOrEmpty(sshPrivateKeyPassphrase)
                    ? new PrivateKeyFile(ms)
                    : new PrivateKeyFile(ms, sshPrivateKeyPassphrase);
                client = new SshClient(sshHost, sshPort, sshUser, key);
            }
        }
        else
        {
            client = new SshClient(sshHost, sshPort, sshUser, sshPassword);
        }

        var forwardedPort = new ForwardedPortLocal(
            "127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);

        await Task.Run(() =>
        {
            client.Connect();
            client.AddForwardedPort(forwardedPort);
            forwardedPort.Start();
        }, ct);

        _tunnels[localPort] = new TunnelEntry(client, forwardedPort);
        return localPort;
    }

    /// <summary>关闭指定本地端口的隧道</summary>
    public bool CloseTunnel(int localPort)
    {
        if (_tunnels.TryRemove(localPort, out var entry))
        {
            entry.ForwardedPort.Stop();
            entry.Client.Disconnect();
            entry.Client.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>列出所有活动隧道</summary>
    public IReadOnlyList<TunnelInfo> ListTunnels()
        => _tunnels.Select(kv => new TunnelInfo
        {
            LocalPort = kv.Key,
            RemoteHost = kv.Value.ForwardedPort.BoundHost,
            RemotePort = (int)kv.Value.ForwardedPort.BoundPort,
            IsConnected = kv.Value.Client.IsConnected,
        }).ToList();

    public void Dispose()
    {
        foreach (var entry in _tunnels.Values)
        {
            try { entry.ForwardedPort.Stop(); } catch { }
            try { entry.Client.Disconnect(); } catch { }
            try { entry.Client.Dispose(); } catch { }
        }
        _tunnels.Clear();
    }

    private sealed record TunnelEntry(SshClient Client, ForwardedPortLocal ForwardedPort);
}

/// <summary>隧道信息（对外暴露）</summary>
public class TunnelInfo
{
    public int LocalPort { get; set; }
    public string RemoteHost { get; set; } = "";
    public int RemotePort { get; set; }
    public bool IsConnected { get; set; }
}
