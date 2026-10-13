namespace DbfToPostgre;

public interface IDbfReader
{
    IDbfFile Open(string path);
}

public interface IDbfFile : IDisposable
{
    IReadOnlyList<DbfField> Fields { get; }
    IAsyncEnumerable<object?[]> ReadRowsAsync(CancellationToken cancellationToken = default);
}

public sealed record DbfField(string Name, char Type, int Length, int DecimalCount);
