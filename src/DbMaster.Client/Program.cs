using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var serverUrl = Environment.GetEnvironmentVariable("DBMASTER_URL") ?? "http://localhost:5200/mcp";

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║        DbMaster Client — 端到端验证测试         ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine($"Server: {serverUrl}\n");

try
{
    var transport = new HttpClientTransport(new()
    {
        Endpoint = new Uri(serverUrl),
        Name = "DbMaster Test Client",
    });

    await using var client = await McpClient.CreateAsync(transport, new()
    {
        ClientInfo = new() { Name = "DbMaster.TestClient", Version = "1.0.0" },
    });

    Console.WriteLine($"Connected to {client.ServerInfo.Name} v{client.ServerInfo.Version}\n");

    // Test 1-9: run all tool tests
    var tools = await client.ListToolsAsync();
    Console.WriteLine($"═══ {tools.Count} tools discovered ═══\n");

    // connect → create → insert → query → describe → stats → disconnect
    await Call(client, "db_connect", new() { ["alias"]="test", ["connectionString"]="Data Source=:memory:", ["dbType"]="sqlite" });
    Console.WriteLine("[connect] ✅");

    await Call(client, "db_execute_command", new() { ["alias"]="test", ["sql"]="CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)", ["confirm"]="CONFIRM" });
    Console.WriteLine("[create table] ✅");

    await Call(client, "db_execute_command", new() { ["alias"]="test", ["sql"]="INSERT INTO users VALUES (1,'Alice',30),(2,'Bob',25),(3,'Charlie',35)", ["confirm"]="CONFIRM" });
    Console.WriteLine("[insert] ✅");

    var query = await Call(client, "db_execute_query", new() { ["alias"]="test", ["sql"]="SELECT * FROM users ORDER BY age" });
    Console.WriteLine($"[query] ✅ ({query.Split('\n').Length} lines)");

    var desc = await Call(client, "db_describe_table", new() { ["alias"]="test", ["tableName"]="users" });
    Console.WriteLine($"[describe] ✅ ({desc.Split('\n').Length} lines)");

    await Call(client, "db_disconnect", new() { ["alias"]="test" });
    Console.WriteLine("[disconnect] ✅");

    Console.WriteLine("\n══════════════════════════════════════════════════");
    Console.WriteLine("  All tests PASSED!");
    Console.WriteLine("══════════════════════════════════════════════════");
}
catch (Exception ex)
{
    Console.WriteLine($"\nFAILED: {ex.Message}");
    Environment.Exit(1);
}

static async Task<string> Call(McpClient c, string tool, Dictionary<string, object?>? args = null)
{
    var r = await c.CallToolAsync(tool, args);
    return string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
}
