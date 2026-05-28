using DbMaster.Core;
using Xunit;

namespace DbMaster.Tests;

/// <summary>
/// AdapterFactory 单元测试。
/// </summary>
[Collection("AdapterRegistration")]
public class AdapterFactoryTests
{
    [Fact]
    public void Create_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => AdapterFactory.Create(""));
    }

    [Fact]
    public void Create_InvalidConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(() => AdapterFactory.Create("not-a-connection-string"));
    }

    [Fact]
    public void Create_NoRegisteredAdapters_Throws()
    {
        // Note: Adapters are registered by ConnectionManagerTests static constructor.
        // This test verifies the error message when no adapter matches.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdapterFactory.Create("SomeUnknown=connection;string=value"));
        Assert.Contains("Unable to auto-detect", ex.Message);
    }

    [Fact]
    public void Register_NullDetector_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AdapterFactory.Register(null!));
    }

    [Fact]
    public void Create_MatchingDetector_ReturnsAdapter()
    {
        bool called = false;
        AdapterFactory.Register(cs =>
        {
            called = true;
            return AdapterFactory.HasKeyword(cs, "TestDetector")
                ? new FakeAdapter("test") : null;
        });

        // 使用不会被真实适配器匹配的连接串
        var adapter = AdapterFactory.Create("TestDetector=only;For=this;Test=true");
        Assert.NotNull(adapter);
        Assert.Equal("test", adapter.DbType);
        Assert.True(called);
    }

    [Fact]
    public void Create_FirstDetectorWins()
    {
        AdapterFactory.Register(cs =>
            AdapterFactory.HasKeyword(cs, "FirstOnly") ? new FakeAdapter("first") : null);
        AdapterFactory.Register(cs =>
            AdapterFactory.HasKeyword(cs, "SecondOnly") ? new FakeAdapter("second") : null);

        // 仅匹配 first 的连接串
        var adapter = AdapterFactory.Create("FirstOnly=true;X=y");
        Assert.Equal("first", adapter.DbType);
    }

    [Fact]
    public void Create_SkippedNull_GoesToNext()
    {
        AdapterFactory.Register(_ => null); // Skip
        AdapterFactory.Register(cs =>
            AdapterFactory.HasKeyword(cs, "SkipMe") ? new FakeAdapter("second") : null);

        var adapter = AdapterFactory.Create("SkipMe=true;Data=x");
        Assert.Equal("second", adapter.DbType);
    }

    [Fact]
    public void HasKeyword_FindsKeyword()
    {
        Assert.True(AdapterFactory.HasKeyword("Server=localhost;Database=db", "Server"));
        Assert.True(AdapterFactory.HasKeyword("Data Source=test.db", "Data Source"));
        Assert.False(AdapterFactory.HasKeyword("Data Source=test.db", "Server"));
    }

    [Fact]
    public void HasKeyword_InvalidConnString_ReturnsFalse()
    {
        Assert.False(AdapterFactory.HasKeyword("", "Server"));
    }

    // ================================================================
    // Fake adapter for testing
    // ================================================================

    private sealed class FakeAdapter : IDbAdapter
    {
        private readonly string _type;
        public FakeAdapter(string type) => _type = type;
        public string DbType => _type;
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default) => Task.FromResult(new QueryResult());
        public Task<int> ExecuteAsync(string sql, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableInfo>>([]);
        public Task<TableSchema> DescribeTableAsync(string tableName, CancellationToken ct = default) => Task.FromResult(new TableSchema());
        public Task<QueryResult> ExplainAsync(string sql, int maxRows, CancellationToken ct = default) => Task.FromResult(new QueryResult());
        public void Dispose() { }
    }
}
