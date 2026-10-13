namespace DbfToPostgre;

public interface IPostgresWriter
{
    Task<long> ImportAsync(IDbfFile file, CancellationToken cancellationToken = default);
}
