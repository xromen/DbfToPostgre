namespace DbfToPostgre;

public interface IImportService
{
    Task<ImportResult> ImportAsync(CancellationToken cancellationToken = default);
}

public sealed record ImportResult(int FileCount, long RowCount);
