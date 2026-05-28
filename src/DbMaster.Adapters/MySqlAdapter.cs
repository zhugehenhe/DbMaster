using System.Data.Common;
using DbMaster.Core;
using MySqlConnector;

namespace DbMaster.Adapters;

public sealed class MySqlAdapter : BaseDbAdapter
{
    static MySqlAdapter()
    {
        AdapterFactory.Register(cs =>
        {
            // MySQL: Server= 但无 TrustServerCertificate（区分 SQL Server）
            if (AdapterFactory.HasKeyword(cs, "Server") &&
                !AdapterFactory.HasKeyword(cs, "TrustServerCertificate") &&
                !AdapterFactory.HasKeyword(cs, "Integrated Security"))
                return new MySqlAdapter(cs);
            return null;
        });
    }

    public MySqlAdapter(string cs) : base(cs) { }
    public override string DbType => "mysql";
    protected override DbConnection CreateConnection() => new MySqlConnection(ConnectionString);

    protected override async Task<List<TableInfo>> QueryTablesAsync(DbConnection conn, CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, TABLE_ROWS, TABLE_COMMENT FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            tables.Add(new TableInfo { Name = r.GetString(0), RowCount = r.GetInt64(1), Comment = r.IsDBNull(2) ? null : r.GetString(2) });
        return tables;
    }

    protected override async Task<List<ColumnInfo>> QueryColumnsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<ColumnInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_COMMENT, COLUMN_KEY FROM information_schema.columns WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION";
        cmd.Parameters.Add(new MySqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cols.Add(new ColumnInfo
            {
                Name = r.GetString(0), DataType = r.GetString(1),
                IsNullable = r.GetString(2) == "YES", IsPrimaryKey = r.GetString(5) == "PRI",
                DefaultValue = r.IsDBNull(3) ? null : r.GetString(3),
                Comment = r.IsDBNull(4) ? null : r.GetString(4),
            });
        return cols;
    }

    protected override async Task<List<string>> QueryPrimaryKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var pks = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.columns WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_KEY = 'PRI'";
        cmd.Parameters.Add(new MySqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) pks.Add(r.GetString(0));
        return pks;
    }

    protected override async Task<List<ForeignKeyInfo>> QueryForeignKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var fks = new List<ForeignKeyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.key_column_usage WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND REFERENCED_TABLE_NAME IS NOT NULL";
        cmd.Parameters.Add(new MySqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            fks.Add(new ForeignKeyInfo { ColumnName = r.GetString(0), ReferencedTable = r.GetString(1), ReferencedColumn = r.GetString(2) });
        return fks;
    }

    protected override async Task<List<IndexInfo>> QueryIndexesAsync(DbConnection conn, string table, CancellationToken ct)
    {
        ValidateTableName(table);
        var indexes = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW INDEX FROM `" + table + "`";
        using var r = await cmd.ExecuteReaderAsync(ct);
        var idxMap = new Dictionary<string, List<string>>();
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(2);
            if (!idxMap.ContainsKey(name))
                idxMap[name] = new List<string>();
            idxMap[name].Add(r.GetString(4));
        }
        indexes.AddRange(idxMap.Select(kv => new IndexInfo { Name = kv.Key, Columns = kv.Value, IsUnique = false }));
        return indexes;
    }

    protected override async Task<string?> QueryCreateSqlAsync(DbConnection conn, string table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE `{table.Replace("`", "``")}`";
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? r.GetString(1) : null;
    }
}
