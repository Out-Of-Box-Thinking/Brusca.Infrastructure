# Brusca.Infrastructure

Concrete infrastructure implementations for the Brusca AI-powered file organizer. This library implements every interface defined in [`Brusca.Core`](../Brusca.Core) using real I/O providers: SQL Server via Dapper, Claude via Anthropic.SDK, Serilog for error logging, and Audit.NET for audit logging.

---

## What lives here

| Folder | Contents |
|--------|----------|
| `Data/Repositories/` | Dapper-based implementations of `ICleaningRepository`, `IFileExtensionRepository`, `IPromptStepRepository`, `IPromptStepCommandRepository` — stored procedures only, no inline SQL |
| `Services/` | `CleaningService`, `FileExtensionService`, `FileSystemService`, `TreeProjectionService` |
| `Claude/` | `ClaudePromptService` — wraps `Anthropic.SDK` |
| `Logging/` | `SerilogErrorLogger` (→ SQL + file), `AuditNetAuditLogger` (→ SQL + file) |
| `Configuration/` | `InfrastructureRegistration` — `IServiceCollection` extension that registers everything |

---

## Dependencies

| Package | Purpose |
|---------|---------|
| `Brusca.Core` | Domain interfaces and models (NuGet) |
| `Dapper` | Stored-procedure-only data access |
| `Microsoft.Data.SqlClient` | SQL Server driver |
| `Audit.NET.SqlServer` | Audit log sink |
| `Serilog` + sinks | Error / structured logging |
| `Anthropic.SDK` | Claude AI integration |
| `DocumentFormat.OpenXml` | `.docx` / `.xlsx` content reading |
| `PdfPig` | `.pdf` content reading |
| `Microsoft.AspNetCore.DataProtection` | Encryption helpers |
| `FluentResults` + `Ardalis.GuardClauses` | Shared utilities |

---

## Database convention

All database access calls stored procedures using the naming convention:

```
schema.usp_Entity_Action    e.g.  cleaning.usp_Cleaning_Create
```

The application SQL login has **EXECUTE-only** permission — no direct table access. See the [`Brusca.Api`](../Brusca.Api) repo for all SQL scripts.

---

## Dependency setup (local development)

This library depends on `Brusca.Core` as a **NuGet package** resolved from the shared local feed at `../nupkgs`.

```powershell
# 1. Pack Brusca.Core first
cd ..\Brusca.Core
.\pack.ps1 -Version 1.0.0

# 2. Restore and build Infrastructure
cd ..\Brusca.Infrastructure
dotnet restore
dotnet build
```

### Pack Infrastructure for downstream consumers

```powershell
.\pack.ps1 -Version 1.0.0
```

---

## Versioning & NuGet

Published as the **`Brusca.Infrastructure`** NuGet package. Bump the version in `Brusca.Infrastructure.csproj` and re-run `pack.ps1` before consuming downstream.

---

## Target framework

`.NET 9` — `net9.0`

---

## Related repositories

| Repo | Role |
|------|------|
| [Brusca.Core](../Brusca.Core) | Domain kernel — interfaces and models |
| [Brusca.Api](../Brusca.Api) | ASP.NET Core 9 host, REST API |
| [Brusca.Tests](../Brusca.Tests) | xUnit integration and unit tests |
