using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models;
using Brusca.Infrastructure.Claude;
using Brusca.Infrastructure.Data.Repositories;
using Brusca.Infrastructure.Encryption;
using Brusca.Infrastructure.Logging;
using Brusca.Infrastructure.Pii;
using Brusca.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brusca.Infrastructure.Configuration;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddBruscaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BruscaOptions>(configuration.GetSection("Brusca"));

        // Repositories
        services.AddScoped<ICleaningRepository, CleaningRepository>();
        services.AddScoped<IFileExtensionRepository, FileExtensionRepository>();
        services.AddScoped<IPromptStepRepository, PromptStepRepository>();
        services.AddScoped<IPromptStepCommandRepository, PromptStepCommandRepository>();
        services.AddScoped<IRedactedFileRepository, RedactedFileRepository>();
        services.AddScoped<IStructurePlanRepository, StructurePlanRepository>();
        services.AddScoped<IFileRelocationRepository, FileRelocationRepository>();

        // Services
        services.AddScoped<ICleaningService, CleaningService>();
        services.AddScoped<IFileSystemService, FileSystemService>();
        services.AddScoped<IFileExtensionService, FileExtensionService>();
        services.AddScoped<ITreeProjectionService, TreeProjectionService>();
        services.AddScoped<IPiiRedactionService, RegexPiiRedactionService>();
        services.AddScoped<IPiiRehydrationService, PiiRehydrationService>();
        services.AddScoped<IDocumentTypeClassifier, HeuristicDocumentTypeClassifier>();
        services.AddScoped<IStructureExecutionService, StructureExecutionService>();
        services.AddSingleton<IFileHashService, Sha256FileHashService>();

        // Image redaction is Windows-only (GDI+). Register only on Windows so
        // non-Windows hosts can substitute their own IImageRedactionService.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IImageRedactionService, GdiImageRedactionService>();

        // Encryption — ASP.NET Core Data Protection seals the PII JSON column.
        var pii = configuration.GetSection("Brusca:Pii").Get<PiiOptions>() ?? new PiiOptions();
        var dpBuilder = services.AddDataProtection()
            .SetApplicationName(pii.DataProtectionApplicationName);
        if (!string.IsNullOrWhiteSpace(pii.KeyRingDirectory))
            dpBuilder.PersistKeysToFileSystem(new DirectoryInfo(pii.KeyRingDirectory));
        services.AddSingleton<IEncryptionService, DataProtectionEncryptionService>();

        // Claude
        services.AddSingleton<ClaudePromptService>();
        services.AddSingleton<IClaudeStructureService, ClaudeStructureService>();

        // Logging
        services.AddSingleton<IErrorLogger, SerilogErrorLogger>();
        services.AddSingleton<IAuditLogger, AuditNetAuditLogger>();
        services.AddBruscaLogging(configuration);

        return services;
    }
}
