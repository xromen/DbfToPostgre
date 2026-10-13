using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DbfToPostgre;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDbfImport(this IServiceCollection services, IConfiguration configuration)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        services.AddOptions<ImportOptions>()
            .Bind(configuration.GetSection(ImportOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.FolderPath), "Import:FolderPath обязателен.")
            .Validate(o => IsConnectionStringValid(o.ConnectionString), "Import:ConnectionString должна содержать Host и Database.")
            .Validate(o => IsIdentifierValid(o.SchemaName) && IsIdentifierValid(o.TableName), "Некорректное имя схемы или таблицы.")
            .Validate(o => IsEncodingValid(o.EncodingCodePage), "Неизвестная кодировка DBF.")
            .ValidateOnStart();
        services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<ImportOptions>>().Value.ConnectionString));
        services.AddTransient<IDbfReader, DbfReader>();
        services.AddTransient<IPostgresWriter, PostgresWriter>();
        services.AddTransient<IImportService, ImportService>();
        return services;
    }

    private static bool IsConnectionStringValid(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return !string.IsNullOrWhiteSpace(builder.Host) && !string.IsNullOrWhiteSpace(builder.Database);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsIdentifierValid(string name)
    {
        try { PostgresSchema.ValidateIdentifier(name); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsEncodingValid(int codePage)
    {
        try { _ = Encoding.GetEncoding(codePage); return true; }
        catch (ArgumentException) { return false; }
    }
}
