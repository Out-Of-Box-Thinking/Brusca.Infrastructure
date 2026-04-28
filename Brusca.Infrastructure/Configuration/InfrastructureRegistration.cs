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
using Brusca.Infrastructure.Services.Metadata;
using Brusca.Infrastructure.Services.PathAccess;
using Brusca.Infrastructure.Services.Trash;
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
        services.AddScoped<IPromotionRepository, PromotionRepository>();
        services.AddScoped<IPathCredentialRepository, PathCredentialRepository>();

        // Services
        services.AddScoped<ICleaningService, CleaningService>();
        services.AddScoped<IFileSystemService, FileSystemService>();
        services.AddScoped<IFileExtensionService, FileExtensionService>();
        services.AddScoped<ITreeProjectionService, TreeProjectionService>();
        services.AddScoped<IPiiRedactionService, RegexPiiRedactionService>();
        services.AddScoped<IPiiRehydrationService, PiiRehydrationService>();
        services.AddScoped<IDocumentTypeClassifier, HeuristicDocumentTypeClassifier>();
        services.AddScoped<IStructureExecutionService, StructureExecutionService>();
        services.AddScoped<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IFileHashService, Sha256FileHashService>();

        // OCR — default fallback returns empty results so the pipeline degrades
        // gracefully when no real engine is wired in. Hosts wanting Tesseract /
        // similar override this binding via TryAddSingleton/Replace.
        services.AddSingleton<IOcrService, NoOpOcrService>();

        // File metadata stripper — composite delegates to extension-specific
        // implementations registered alongside it.
        services.AddSingleton<OpenXmlMetadataStripper>();
        services.AddSingleton<PdfMetadataStripper>();
        services.AddSingleton<IFileMetadataStripper>(sp =>
        {
            var children = new List<IFileMetadataStripper>
            {
                sp.GetRequiredService<OpenXmlMetadataStripper>(),
                sp.GetRequiredService<PdfMetadataStripper>(),
            };
            if (OperatingSystem.IsWindows())
                children.Add(new ImageMetadataStripper());
            return new CompositeFileMetadataStripper(children);
        });

        // Image redaction is Windows-only (GDI+). Register only on Windows so
        // non-Windows hosts can substitute their own IImageRedactionService.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IImageRedactionService, GdiImageRedactionService>();

        // Cross-platform "send to trash" — Windows recycle bin / freedesktop
        // (~/.local/share/Trash) / macOS Finder. PromotionService consumes this
        // via DI so it works the same on every host.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<ITrashService, WindowsRecycleBinTrashService>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<ITrashService, MacOsTrashService>();
        else
            services.AddSingleton<ITrashService, LinuxFreedesktopTrashService>();

        // Promotion is now cross-platform via ITrashService.
        services.AddScoped<IPromotionService, PromotionService>();

        // Path access — one IPlatformShareMounter per OS, plus a single
        // DefaultPathAccessService that probes / mounts / persists
        // credentials encrypted via IEncryptionService.
#pragma warning disable CA1416 // platform-specific implementations are guarded at runtime
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformShareMounter, WindowsShareMounter>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformShareMounter, MacOsShareMounter>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IPlatformShareMounter, LinuxShareMounter>();
#pragma warning restore CA1416
        services.AddScoped<IPathAccessService, DefaultPathAccessService>();

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
