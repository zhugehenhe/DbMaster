using System.ComponentModel;
using DbMaster.Core;
using ModelContextProtocol.Server;

namespace DbMaster.Server.Tools;

/// <summary>
/// SSH 隧道 MCP 工具集 — 通过 SSH 端口转发访问无公网端口的远程数据库。
/// 建立隧道后用原有的 db_connect 工具连接 localhost:端口 即可。
/// </summary>
[McpServerToolType]
public sealed class SshTools
{
    private readonly SshTunnelManager _tunnel;

    public SshTools(SshTunnelManager tunnelManager)
    {
        _tunnel = tunnelManager;
    }

    [McpServerTool(Name = "db_ssh_tunnel"),
     Description("Create an SSH tunnel to a remote server. Use the returned local port with db_connect to access databases without public ports.")]
    public async Task<string> DbSshTunnel(
        [Description("SSH server hostname or IP")] string sshHost,
        [Description("SSH username")] string sshUser,
        [Description("SSH password")] string sshPassword,
        [Description("Remote database host (from SSH server's perspective, usually 127.0.0.1)")]
        string remoteHost,
        [Description("Remote database port, e.g. 3306 for MySQL, 5432 for PostgreSQL")] int remotePort,
        [Description("SSH port, default 22")] int sshPort = 22,
        CancellationToken ct = default)
    {
        try
        {
            var localPort = await _tunnel.CreateTunnelAsync(
                sshHost, sshPort, sshUser, sshPassword, remoteHost, remotePort, ct);

            return $"SSH tunnel established!\n" +
                   $"  SSH: {sshUser}@{sshHost}:{sshPort}\n" +
                   $"  Tunnel: localhost:{localPort} → {remoteHost}:{remotePort}\n" +
                   $"  Next: db_connect(alias=\"...\", connStr=\"Server=127.0.0.1;Port={localPort};Database=...;...\", dbType=\"...\")";
        }
        catch (Exception ex)
        {
            return $"SSH tunnel failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_ssh_disconnect"),
     Description("Close an SSH tunnel by its local port number.")]
    public string DbSshDisconnect(
        [Description("Local port number of the tunnel to close")] int localPort)
    {
        return _tunnel.CloseTunnel(localPort)
            ? $"SSH tunnel on port {localPort} closed."
            : $"No tunnel found on port {localPort}.";
    }

    [McpServerTool(Name = "db_ssh_list"),
     Description("List all active SSH tunnels.")]
    public string DbSshList()
    {
        var tunnels = _tunnel.ListTunnels();
        if (tunnels.Count == 0)
            return "No active SSH tunnels.";

        return "Active SSH tunnels:\n" + string.Join("\n", tunnels.Select(t =>
            $"  localhost:{t.LocalPort} → {t.RemoteHost}:{t.RemotePort} ({(t.IsConnected ? "connected" : "disconnected")})"));
    }
}
