# Brusca.Infrastructure

Concrete infrastructure implementations for the Brusca AI-powered file organizer. This library implements every interface defined in [`Brusca.Core`](https://github.com/Out-Of-Box-Thinking/Brusca.Core) using real I/O providers

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

The application SQL login has **EXECUTE-only** permission — no direct table access. See the [`Brusca.Api`](https://github.com/Out-Of-Box-Thinking/Brusca.Api) repo for all SQL scripts.

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

## Continuous integration

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Push or PR to `main` | `dotnet restore` → `dotnet build` → `dotnet pack` → uploads `*.nupkg` artifact |
| [`.github/workflows/release.yml`](.github/workflows/release.yml) | Push of a `v*` tag | Packs with version derived from the tag (strips leading `v`) and pushes to **GitHub Packages**; also pushes to **NuGet.org** when the `NUGET_API_KEY` secret is set |

### Cutting a release

```powershell
# Bump <Version> in Brusca.Infrastructure/Brusca.Infrastructure.csproj first if you want it embedded in the assembly
git tag v1.0.1
git push origin v1.0.1
```

The release workflow uses the built-in `GITHUB_TOKEN` to push to GitHub Packages — no extra setup required. To also publish to NuGet.org, add a repo secret named `NUGET_API_KEY`.

---

## Related repositories

| Repo | Role |
|------|------|
| [Brusca.Core](https://github.com/Out-Of-Box-Thinking/Brusca.Core) | Domain kernel — interfaces and models |
| [Brusca.Api](https://github.com/Out-Of-Box-Thinking/Brusca.Api) | ASP.NET Core 9 host, REST API |
| [Brusca.Tests](https://github.com/Out-Of-Box-Thinking/Brusca.Tests) | xUnit integration and unit tests |
| [Brusca.Web](https://github.com/Out-Of-Box-Thinking/Brusca.Web) | Astro 5 front-end |


---

## PII redaction & structure planning (NEW)

Brusca now strips PII from every file BEFORE any data is sent to Claude, encrypts the original PII at rest, and applies a Claude-designed directory layout to either the source path or an alternate path \u2014 recording the before/after of every operation for full audit traceability.

### Pipeline at a glance

1. **Read** \u2014 a supported reader pulls file content into memory.
2. **Redact** \u2014 `IPiiRedactionService` replaces every PII span with a stable token (`[[PII:Kind:NNNN]]`).
3. **Classify** \u2014 `IDocumentTypeClassifier` assigns a `DocumentType` from the redacted text + extension.
4. **Encrypt** \u2014 `IEncryptionService` (ASP.NET Core Data Protection) seals the PII JSON into a database column.
5. **Plan** \u2014 `IClaudeStructureService` calls Claude with ONLY `DocumentType` + extension counts; receives a `DirectoryStructurePlan` of folder/file templates.
6. **Execute** \u2014 `IStructureExecutionService` decrypts the PII in memory, fills the templates, performs the move/rename/create, and records every before/after path into `cleaning.FileRelocation`.

### Three new database tables

- `cleaning.RedactedFile`  \u2014 redacted descriptor + encrypted PII column.
- `cleaning.StructurePlan` \u2014 Claude-generated layout.
- `cleaning.FileRelocation`\u2014 before/after audit log.

DDL is in `Brusca.Core/docs/DeveloperGuide.md`.

### Five new API endpoints

- `POST /api/cleanings/{id}/redact`
- `POST /api/cleanings/{id}/generate-structure`
- `GET  /api/cleanings/{id}/structure-plan`
- `POST /api/cleanings/{id}/execute-structure`
- `GET  /api/cleanings/{id}/relocations`

Full docs in `Brusca.Api/docs/UserGuide.md`.

---

## Documentation index

| Topic | Location |
|-------|----------|
| Step-by-step install | `docs/InstallGuide.md` |
| End-user / integrator guide | `docs/UserGuide.md` |
| Engineer / contributor guide | `docs/DeveloperGuide.md` |

For the cross-repo pipeline overview, start in `Brusca.Core/docs/UserGuide.md` then walk down through `Brusca.Infrastructure`, `Brusca.Api`, `Brusca.Web`, and `Brusca.Tests`.
