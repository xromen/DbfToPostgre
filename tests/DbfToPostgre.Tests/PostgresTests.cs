using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DbfToPostgre.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBF_TEST_CONNECTION_STRING")))
            Skip = "Задайте DBF_TEST_CONNECTION_STRING для проверки с PostgreSQL.";
    }
}

public sealed class PostgresTests
{
    [PostgresFact]
    public async Task AddsColumnsMapsByNameAndWritesExplicitNullsInsteadOfDefaults()
    {
        using var fixture = new DbfFixture();
        var firstPath = fixture.Write("first.dbf", secondName: "");
        var changedFields = new[] { DbfFixture.Fields[4], new DbfField("EXTRA", 'C', 20, 0), DbfFixture.Fields[0] };
        var secondPath = fixture.Write("second.dbf", fields: changedFields, secondName: "   ");
        var settings = Options.Create(new ImportOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable("DBF_TEST_CONNECTION_STRING")!,
            TableName = "dbf_test_" + Guid.NewGuid().ToString("N")
        });
        await using var dataSource = NpgsqlDataSource.Create(settings.Value.ConnectionString);
        var writer = new PostgresWriter(dataSource, settings);
        var reader = new DbfReader(settings);
        var table = $"\"public\".\"{settings.Value.TableName}\"";
        try
        {
            using (var first = reader.Open(firstPath))
                Assert.Equal(2, await writer.ImportAsync(first));
            await using var defaults = dataSource.CreateCommand(
                $"ALTER TABLE {table} ADD COLUMN legacy text DEFAULT 'fallback', ALTER COLUMN amount SET DEFAULT 77");
            await defaults.ExecuteNonQueryAsync();
            using (var second = reader.Open(secondPath))
                Assert.Equal(2, await writer.ImportAsync(second));

            await using var data = dataSource.CreateCommand(
                $"SELECT id, name, amount, date, active, count, extra, legacy FROM {table} ORDER BY id");
            await using (var result = await data.ExecuteReaderAsync())
            {
                Assert.True(await result.ReadAsync());
                Assert.Equal(1L, result.GetInt64(0));
                Assert.Equal(1234.56m, result.GetDecimal(2));
                Assert.True(result.IsDBNull(6));
                Assert.Equal("fallback", result.GetString(7));

                Assert.True(await result.ReadAsync());
                Assert.True(result.IsDBNull(1));
                Assert.True(result.IsDBNull(6));

                Assert.True(await result.ReadAsync());
                Assert.Equal(3L, result.GetInt64(0));
                Assert.Equal("Привет, мир", result.GetString(1));
                Assert.True(result.IsDBNull(2));
                Assert.True(result.IsDBNull(3));
                Assert.True(result.IsDBNull(4));
                Assert.Equal(12345678901234567890m, result.GetDecimal(5));
                Assert.Equal("Новое", result.GetString(6));
                Assert.True(result.IsDBNull(7));

                Assert.True(await result.ReadAsync());
                Assert.True(result.IsDBNull(1));
                Assert.True(result.IsDBNull(5));
                Assert.False(await result.ReadAsync());
            }

            var failedPath = fixture.Write("failed.dbf", fields: [.. changedFields, new("UNCOMMIT", 'C', 20, 0)]);
            using var source = reader.Open(failedPath);
            using var failed = new FailedFile(source);
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.ImportAsync(failed));
            await using var schema = dataSource.CreateCommand("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table AND column_name = 'uncommit'
                """);
            schema.Parameters.AddWithValue("table", settings.Value.TableName);
            Assert.Equal(0L, await schema.ExecuteScalarAsync());

            await using var required = dataSource.CreateCommand(
                $"ALTER TABLE {table} ADD COLUMN required text NOT NULL DEFAULT 'value'");
            await required.ExecuteNonQueryAsync();
            using var incompatible = reader.Open(secondPath);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => writer.ImportAsync(incompatible));
            Assert.Contains("NOT NULL", exception.Message);
            await using var count = dataSource.CreateCommand($"SELECT count(*) FROM {table}");
            Assert.Equal(4L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP TABLE IF EXISTS {table}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task CreatesTableCombinesFilesGeneratesIdsAndRollsBackFailedFile()
    {
        using var fixture = new DbfFixture();
        fixture.Write("first.dbf");
        fixture.Write("second.DBF");
        var settings = Options.Create(new ImportOptions
        {
            FolderPath = fixture.Folder,
            ConnectionString = Environment.GetEnvironmentVariable("DBF_TEST_CONNECTION_STRING")!,
            TableName = "dbf_test_" + Guid.NewGuid().ToString("N")
        });
        await using var dataSource = NpgsqlDataSource.Create(settings.Value.ConnectionString);
        var writer = new PostgresWriter(dataSource, settings);
        var reader = new DbfReader(settings);
        var service = new ImportService(settings, reader, writer, NullLogger<ImportService>.Instance);
        var table = $"\"public\".\"{settings.Value.TableName}\"";
        try
        {
            Assert.Equal(new ImportResult(2, 4), await service.ImportAsync());
            await using var command = dataSource.CreateCommand($"SELECT count(*), count(DISTINCT id), min(id), max(id) FROM {table}");
            await using (var result = await command.ExecuteReaderAsync())
            {
                Assert.True(await result.ReadAsync());
                Assert.Equal(4, result.GetInt64(0));
                Assert.Equal(4, result.GetInt64(1));
                Assert.Equal(1, result.GetInt64(2));
                Assert.Equal(4, result.GetInt64(3));
            }
            await using var data = dataSource.CreateCommand($"SELECT name, amount, date, active, count FROM {table} ORDER BY id LIMIT 1");
            await using (var result = await data.ExecuteReaderAsync())
            {
                Assert.True(await result.ReadAsync());
                Assert.Equal("Привет, мир", result.GetString(0));
                Assert.Equal(1234.56m, result.GetDecimal(1));
                Assert.Equal(new DateTime(2026, 10, 13), result.GetDateTime(2));
                Assert.True(result.GetBoolean(3));
                Assert.Equal(12345678901234567890m, result.GetDecimal(4));
            }

            using var source = reader.Open(Path.Combine(fixture.Folder, "first.dbf"));
            using var failed = new FailedFile(source);
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.ImportAsync(failed));
            await using var count = dataSource.CreateCommand($"SELECT count(*) FROM {table}");
            Assert.Equal(4L, await count.ExecuteScalarAsync());

            await using var alter = dataSource.CreateCommand($"ALTER TABLE {table} ALTER COLUMN amount TYPE text");
            await alter.ExecuteNonQueryAsync();
            using var incompatible = reader.Open(Path.Combine(fixture.Folder, "first.dbf"));
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.ImportAsync(incompatible));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand($"DROP TABLE IF EXISTS {table}");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class FailedFile(IDbfFile source) : IDbfFile
    {
        public IReadOnlyList<DbfField> Fields => source.Fields;
        public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in source.ReadRowsAsync(cancellationToken))
            {
                yield return row;
                throw new InvalidDataException("Имитация повреждения файла после первой строки.");
            }
        }
        public void Dispose() { }
    }
}
