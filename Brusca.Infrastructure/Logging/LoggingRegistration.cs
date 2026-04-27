using Audit.Core;
using Audit.SqlServer.Providers;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Elasticsearch;
using Serilog.Sinks.MSSqlServer;

namespace Brusca.Infrastructure.Logging;

public static class LoggingRegistration
{
    /// <summary>
    /// Configures Serilog (error log) and Audit.NET (audit log) from appsettings.json.
    /// The connection string is read from Brusca:DatabaseConnectionString — the single
    /// authoritative location for all database connections in the application.
    /// Call this in Program.cs BEFORE builder.Build().
    /// </summary>
    public static IServiceCollection AddBruscaLogging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var opts = configuration.GetSection("Brusca").Get<BruscaOptions>() ?? new();

        // All database connections use the single connection string in Brusca:DatabaseConnectionString
        var connStr = opts.DatabaseConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException(
                "Brusca:DatabaseConnectionString is required in appsettings.json.");

        ConfigureSerilog(opts.Logging.Error, connStr);
        ConfigureAuditNet(opts.Logging.Audit, connStr);

        return services;
    }

    private static void ConfigureSerilog(LogSinkOptions opts, string connStr)
    {
        var level = Enum.TryParse<LogEventLevel>(opts.MinimumLevel, out var l)
            ? l : LogEventLevel.Warning;

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext();

        switch (opts.Sink)
        {
            case LogSinkTarget.Database:
                config.WriteTo.MSSqlServer(connStr, sinkOptions: ErrorSinkOptions());
                break;
            case LogSinkTarget.File:
                config.WriteTo.File(
                    new ElasticsearchJsonFormatter(),
                    opts.FilePath ?? "logs/error-.json",
                    rollingInterval: RollingInterval.Day);
                break;
            case LogSinkTarget.Both:
                config.WriteTo.MSSqlServer(connStr, sinkOptions: ErrorSinkOptions());
                config.WriteTo.File(
                    new ElasticsearchJsonFormatter(),
                    opts.FilePath ?? "logs/error-.json",
                    rollingInterval: RollingInterval.Day);
                break;
            case LogSinkTarget.Elasticsearch:
                config.WriteTo.Elasticsearch(
                    new Serilog.Sinks.Elasticsearch.ElasticsearchSinkOptions(
                        new Uri(opts.ElasticsearchUri ?? "http://localhost:9200"))
                    {
                        AutoRegisterTemplate = true,
                        IndexFormat = opts.ElasticsearchIndexFormat ?? "brusca-error-{0:yyyy.MM}"
                    });
                break;
        }

        Log.Logger = config.CreateLogger();
    }

    private static MSSqlServerSinkOptions ErrorSinkOptions() => new()
    {
        TableName = "Log",
        SchemaName = "error",
        AutoCreateSqlDatabase = false,
        AutoCreateSqlTable = false
    };

    private static void ConfigureAuditNet(LogSinkOptions opts, string connStr)
    {
        switch (opts.Sink)
        {
            case LogSinkTarget.Database:
            case LogSinkTarget.Both:
                Configuration.DataProvider = new SqlDataProvider(cfg =>
                {
                    cfg.ConnectionString = connStr;
                    cfg.Schema = "audit";
                    cfg.TableName = "Log";
                    cfg.IdColumnName = "Id";
                    cfg.JsonColumnName = "Data";
                });
                break;
            case LogSinkTarget.File:
                Configuration.DataProvider = new Audit.Core.Providers.FileDataProvider(cfg =>
                {
                    cfg.DirectoryPath = opts.FilePath ?? "logs/audit";
                    cfg.FilenameBuilder = ev => $"{ev.EventType}_{ev.StartDate:yyyyMMdd_HHmmss}.json";
                });
                break;
        }
    }
}
