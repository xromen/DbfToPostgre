using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbfToPostgre;

public sealed class ImportService(
    IOptions<ImportOptions> options,
    IDbfReader dbfReader,
    IPostgresWriter postgresWriter,
    ILogger<ImportService> logger) : IImportService
{
    public async Task<ImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var folder = Path.GetFullPath(options.Value.FolderPath);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Папка с DBF не найдена: {folder}");

        var files = Directory.EnumerateFiles(folder)
            .Where(p => Path.GetExtension(p).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            logger.LogWarning("В папке {Folder} нет DBF-файлов.", folder);
            return new ImportResult(0, 0);
        }

        long total = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Импорт файла {File}", path);
            try
            {
                using var file = dbfReader.Open(path);
                var rows = await postgresWriter.ImportAsync(file, cancellationToken);
                total += rows;
                logger.LogInformation("Импортировано {Rows} строк из {File}", rows, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Не удалось импортировать файл: {path}", ex);
            }
        }

        logger.LogInformation("Завершено. Файлов: {Files}, строк: {Rows}", files.Length, total);
        return new ImportResult(files.Length, total);
    }
}
