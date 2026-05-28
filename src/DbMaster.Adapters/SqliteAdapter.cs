using System.Data.Common;
using DbMaster.Core;
using Microsoft.Data.Sqlite;

namespace DbMaster.Adapters;

public sealed class SqliteAdapter : BaseDbAdapter
{
    static SqliteAdapter()
    {
        AdapterFactory.RegisterFactory("sqlite", cs => new SqliteAdapter(cs));
        AdapterFactory.Register(cs =>
        {
            if (AdapterFactory.HasKeyword(cs, "Data Source") &&
                !AdapterFactory.HasKeyword(cs, "Server") &&
                !AdapterFactory.HasKeyword(cs, "Host"))
                return new SqliteAdapter(cs);
            return null;
        });
    }

    public SqliteAdapter(string cs) : base(cs) { }
    public override string DbType => "sqlite";
    protected override DbConnection CreateConnection() => new SqliteConnection(ConnectionString);

    /// <summary>SQLite 使用 EXPLAIN QUERY PLAN 获取执行计划</summary>
    protected override string ExplainPrefix(string sql) => $"EXPLAIN QUERY PLAN {sql}";

    protected override async Task<List<TableInfo>> QueryTablesAsync(DbConnection conn, CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var names = new List<string>();
        using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                names.Add(r.GetString(0));
        }
        foreach (var name in names)
        {
            using var cnt = conn.CreateCommand();
            cnt.CommandText = $"SELECT COUNT(*) FROM \"{name}\"";
            tables.Add(new TableInfo { Name = name, RowCount = Convert.ToInt64(await cnt.ExecuteScalarAsync(ct)) });
        }
        return tables;
    }

    protected override async Task<List<ColumnInfo>> QueryColumnsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<ColumnInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cols.Add(new ColumnInfo
            {
                Name = r.GetString(1), DataType = r.IsDBNull(2) ? "TEXT" : r.GetString(2),
                IsNullable = r.GetInt32(3) == 0, IsPrimaryKey = r.GetInt32(5) != 0,
                DefaultValue = r.IsDBNull(4) ? null : r.GetString(4),
            });
        return cols;
    }

    protected override async Task<List<string>> QueryPrimaryKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var pks = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (r.GetInt32(5) != 0) pks.Add(r.GetString(1));
        return pks;
    }

    protected override async Task<List<ForeignKeyInfo>> QueryForeignKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var fks = new List<ForeignKeyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"")}\")";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            fks.Add(new ForeignKeyInfo { ColumnName = r.GetString(3), ReferencedTable = r.GetString(2), ReferencedColumn = r.GetString(4) });
        return fks;
    }

    protected override async Task<List<IndexInfo>> QueryIndexesAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var indexes = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list(\"{table.Replace("\"", "\"\"")}\")";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var idxName = r.GetString(1);
            var idxCols = new List<string>();
            using var ic = conn.CreateCommand();
            ic.CommandText = $"PRAGMA index_info(\"{idxName.Replace("\"", "\"\"")}\")";
            using var ir = await ic.ExecuteReaderAsync(ct);
            while (await ir.ReadAsync(ct)) idxCols.Add(ir.GetString(2));
            indexes.Add(new IndexInfo { Name = idxName, Columns = idxCols, IsUnique = r.GetInt32(2) != 0 });
        }
        return indexes;
    }

    protected override async Task<string?> QueryCreateSqlAsync(DbConnection conn, string table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.Add(new SqliteParameter("@name", table));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
