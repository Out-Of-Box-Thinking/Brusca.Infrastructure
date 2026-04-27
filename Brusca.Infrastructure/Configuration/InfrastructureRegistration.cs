using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models;
using Brusca.Infrastructure.Claude;
using Brusca.Infrastructure.Data.Repositories;
using Brusca.Infrastructure.Logging;
using Brusca.Infrastructure.Services;
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

        // Services
        services.AddScoped<ICleaningService, CleaningService>();
        services.AddScoped<IFileSystemService, FileSystemService>();
        services.AddScoped<IFileExtensionService, FileExtensionService>();
        services.AddScoped<ITreeProjectionService, TreeProjectionService>();

        // Claude
        services.AddSingleton<ClaudePromptService>();

        // Logging
        services.AddSingleton<IErrorLogger, SerilogErrorLogger>();
        services.AddSingleton<IAuditLogger, AuditNetAuditLogger>();
        services.AddBruscaLogging(configuration);

        return services;
    }
}
