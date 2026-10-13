using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DbfToPostgre.Tests;

public sealed class ImportTests
{
    [Fact]
    public async Task ReadsDos866PreservesLeadingSpacesNumbersAndNullsAndSkipsDeletedRows()
    {
        using var fixture = new DbfFixture();
        using var file = Reader().Open(fixture.Write());
        Assert.Equal(["name", "amount", "date", "active", "count"], file.Fields.Select(f => f.Name));
        var rows = new List<object?[]>();
        await foreach (var row in file.ReadRowsAsync()) rows.Add(row);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Привет, мир", rows[0][0]);
        Assert.Equal(1234.56m, rows[0][1]);
        Assert.Equal(new DateTime(2026, 10, 13), rows[0][2]);
        Assert.Equal(true, rows[0][3]);
        Assert.Equal(12345678901234567890m, rows[0][4]);
        Assert.Equal("  Ёжик", rows[1][0]);
        Assert.All(rows[1].Skip(1), value => Assert.Null(value));
    }

    [Fact]
    public async Task RejectsInvalidNumbersInsteadOfImportingNull()
    {
        using var fixture = new DbfFixture();
        using var file = Reader().Open(fixture.Write(amount: "broken"));
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var row in file.ReadRowsAsync()) { }
        });
    }

    [Fact]
    public void RejectsTruncatedFile()
    {
        using var fixture = new DbfFixture();
        var path = fixture.Write();
        using (var stream = File.OpenWrite(path)) stream.SetLength(stream.Length - 10);
        Assert.Throws<InvalidDataException>(() => Reader().Open(path));
    }

    [Fact]
    public async Task ImportsAllDbfFilesCaseInsensitivelyIntoOneWriter()
    {
        using var fixture = new DbfFixture();
        fixture.Write("first.dbf");
        fixture.Write("second.DBF");
        File.WriteAllText(Path.Combine(fixture.Folder, "ignore.txt"), "ignore");
        var writer = new CountingWriter();
        var result = await Service(fixture, writer).ImportAsync();
        Assert.Equal(new ImportResult(2, 4), result);
        Assert.Equal(2, writer.Calls);
    }

    [Fact]
    public async Task StopsBeforeWritingFileWithDifferentStructure()
    {
        using var fixture = new DbfFixture();
        fixture.Write("first.dbf");
        var path = fixture.Write("second.dbf");
        using (var stream = File.OpenWrite(path))
        {
            stream.Position = 32;
            stream.WriteByte((byte)'X');
        }
        var writer = new CountingWriter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(fixture, writer).ImportAsync());
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("second.dbf", exception.Message);
        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task EmptyFolderDoesNotCallWriter()
    {
        using var fixture = new DbfFixture();
        Directory.CreateDirectory(fixture.Folder);
        var writer = new CountingWriter();
        Assert.Equal(new ImportResult(0, 0), await Service(fixture, writer).ImportAsync());
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeWriting()
    {
        using var fixture = new DbfFixture();
        fixture.Write();
        var writer = new CountingWriter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(fixture, writer).ImportAsync(new CancellationToken(true)));
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public void CreatesBigserialAndQuotesSqlIdentifiers()
    {
        var sql = PostgresSchema.CreateTableSql("public", "order\"; DROP TABLE users;--",
            [new("select", 'C', 20, 0), new("amount", 'N', 12, 2)]);
        Assert.Contains("\"id\" bigserial PRIMARY KEY", sql);
        Assert.Contains("\"order\"\"; DROP TABLE users;--\"", sql);
        Assert.Contains("\"select\" character varying(20)", sql);
        Assert.Contains("\"amount\" numeric(12,2)", sql);
    }

    [Theory]
    [InlineData("id", 'C')]
    [InlineData("binary", 'G')]
    public void RejectsReservedNamesAndUnsupportedTypes(string name, char type)
        => Assert.ThrowsAny<Exception>(() => PostgresSchema.ValidateFields([new(name, type, 10, 0)]));

    [Fact]
    public void ValidatesSettingsAndRegistersReusableImportService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Import:FolderPath"] = "data",
            ["Import:TableName"] = "dbf_data",
            ["Import:ConnectionString"] = "Host=localhost;Database=import_test;Username=test"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddDbfImport(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(866, provider.GetRequiredService<IOptions<ImportOptions>>().Value.EncodingCodePage);
        Assert.IsType<ImportService>(provider.GetRequiredService<IImportService>());

        configuration["Import:TableName"] = "";
        using var invalidProvider = new ServiceCollection().AddLogging().AddDbfImport(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => invalidProvider.GetRequiredService<IOptions<ImportOptions>>().Value);
    }

    private static DbfReader Reader() => new(Options.Create(new ImportOptions()));
    private static ImportService Service(DbfFixture fixture, IPostgresWriter writer) => new(
        Options.Create(new ImportOptions { FolderPath = fixture.Folder }), Reader(), writer, NullLogger<ImportService>.Instance);

    private sealed class CountingWriter : IPostgresWriter
    {
        public int Calls { get; private set; }
        public async Task<long> ImportAsync(IDbfFile file, CancellationToken cancellationToken = default)
        {
            Calls++;
            long count = 0;
            await foreach (var row in file.ReadRowsAsync(cancellationToken)) count++;
            return count;
        }
    }
}
