using Brusca.Core.Contracts.Logging;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Brusca.Infrastructure.Logging;

/// <summary>
/// Error logger backed by Serilog.
/// Sink selection (DB / File / Both / Elasticsearch) is read from:
///   appsettings.json -> Brusca:Logging:Error:Sink
/// Minimum level from:
///   appsettings.json -> Brusca:Logging:Error:MinimumLevel
/// </summary>
public sealed class SerilogErrorLogger : IErrorLogger
{
    private readonly ILogger _log = Log.ForContext<SerilogErrorLogger>();

    public Task LogErrorAsync(
        string message, Exception? ex = null, string? correlationId = null,
        Guid? cleaningId = null, string? userId = null, CancellationToken ct = default)
    {
        _log
            .ForContext("CorrelationId", correlationId)
            .ForContext("CleaningId", cleaningId)
            .ForContext("UserId", userId)
            .Error(ex, message);
        return Task.CompletedTask;
    }

    public Task LogWarningAsync(string message, string? correlationId = null, CancellationToken ct = default)
    {
        _log.ForContext("CorrelationId", correlationId).Warning(message);
        return Task.CompletedTask;
    }

    public Task LogInformationAsync(string message, string? correlationId = null, CancellationToken ct = default)
    {
        _log.ForContext("CorrelationId", correlationId).Information(message);
        return Task.CompletedTask;
    }

    public Task LogCriticalAsync(
        string message, Exception? ex = null, string? correlationId = null, CancellationToken ct = default)
    {
        _log.ForContext("CorrelationId", correlationId).Fatal(ex, message);
        return Task.CompletedTask;
    }
}
