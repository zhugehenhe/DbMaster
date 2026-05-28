using System.Data.Common;
using DbMaster.Core;
using Microsoft.Data.SqlClient;

namespace DbMaster.Adapters;

public sealed class SqlServerAdapter : BaseDbAdapter
{
    static SqlServerAdapter()
    {
        AdapterFactory.Register(cs =>
        {
            if ((AdapterFactory.HasKeyword(cs, "Server") || AdapterFactory.HasKeyword(cs, "Data Source")) &&
                (AdapterFactory.HasKeyword(cs, "TrustServerCertificate") ||
                 AdapterFactory.HasKeyword(cs, "Integrated Security") ||
                 AdapterFactory.HasKeyword(cs, "User Id")))
                return new SqlServerAdapter(cs);
            return null;
        });
    }

    public SqlServerAdapter(string cs) : base(cs) { }
    public override string DbType => "sqlserver";
    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override async Task<List<TableInfo>> QueryTablesAsync(DbConnection conn, CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM information_schema.tables WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            using var cnt = conn.CreateCommand();
            cnt.CommandText = $"SELECT COUNT(*) FROM [{name.Replace("]", "]]")}]";
            tables.Add(new TableInfo { Name = name, RowCount = Convert.ToInt64(await cnt.ExecuteScalarAsync(ct)) });
        }
        return tables;
    }

    protected override async Task<List<ColumnInfo>> QueryColumnsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<ColumnInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM information_schema.columns WHERE TABLE_NAME = @table ORDER BY ORDINAL_POSITION";
        cmd.Parameters.Add(new SqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cols.Add(new ColumnInfo
            {
                Name = r.GetString(0), DataType = r.GetString(1),
                IsNullable = r.GetString(2) == "YES",
                DefaultValue = r.IsDBNull(3) ? null : r.GetString(3),
            });
        return cols;
    }

    protected override async Task<List<string>> QueryPrimaryKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var pks = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.key_column_usage WHERE TABLE_NAME = @table AND CONSTRAINT_NAME LIKE 'PK_%'";
        cmd.Parameters.Add(new SqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) pks.Add(r.GetString(0));
        return pks;
    }

    protected override async Task<List<ForeignKeyInfo>> QueryForeignKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var fks = new List<ForeignKeyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME = kcu.COLUMN_NAME, REFERENCED_TABLE = ccu.TABLE_NAME, REFERENCED_COLUMN = ccu.COLUMN_NAME FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME JOIN information_schema.constraint_column_usage ccu ON ccu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME WHERE tc.CONSTRAINT_TYPE = 'FOREIGN KEY' AND tc.TABLE_NAME = @table";
        cmd.Parameters.Add(new SqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            fks.Add(new ForeignKeyInfo { Name = r.GetString(0), ColumnName = r.GetString(0), ReferencedTable = r.GetString(1), ReferencedColumn = r.GetString(2) });
        return fks;
    }

    protected override async Task<List<IndexInfo>> QueryIndexesAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var indexes = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT i.name, c.name, i.is_unique FROM sys.indexes i JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id WHERE OBJECT_NAME(i.object_id) = @table";
        cmd.Parameters.Add(new SqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        var idxMap = new Dictionary<string, List<string>>();
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (!idxMap.ContainsKey(name))
                idxMap[name] = new List<string>();
            idxMap[name].Add(r.GetString(1));
        }
        indexes.AddRange(idxMap.Select(kv => new IndexInfo { Name = kv.Key, Columns = kv.Value, IsUnique = false }));
        return indexes;
    }
}
