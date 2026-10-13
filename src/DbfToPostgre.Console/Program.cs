using DbfToPostgre;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddDbfImport(builder.Configuration);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Console");
try
{
    await host.StartAsync();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await host.Services.GetRequiredService<IImportService>().ImportAsync(lifetime.ApplicationStopping);
    return 0;
}
catch (OperationCanceledException)
{
    logger.LogWarning("Импорт отменён. Текущий файл не сохранён.");
    return 2;
}
catch (Exception ex)
{
    logger.LogError(ex, "Импорт завершился с ошибкой.");
    return 1;
}
finally
{
    await host.StopAsync();
}
