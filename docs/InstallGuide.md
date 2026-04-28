# Install Guide — Brusca.Infrastructure

Concrete implementations (Dapper, Claude SDK, Serilog, Audit.NET, Data Protection-based encryption, regex PII redactor, heuristic document classifier, structure executor).

---

## 1. Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 9.0+ |
| SQL Server | 2022+ (2025 recommended for `JSON` column type) |
| Anthropic API key | a current key for Claude (`claude-opus-4-5` by default) |

The local NuGet feed at `..\nupkgs` (one level above the repo) is used for the `Brusca.Core` package reference.

---

## 2. Pack `Brusca.Core` first

You cannot restore `Brusca.Infrastructure` until `Brusca.Core` has been packed into the local feed. Do this in `Brusca.Core`:

```powershell
cd \\OOBT-NAS\Workstation\Repo\Brusca.Core
.\pack.ps1 -Version 1.0.999-pii
```

Then pin the version in this repo (already pinned to `1.0.999-pii` after the PII migration):

```xml
<PackageReference Include="Brusca.Core" Version="1.0.999-pii" />
```

Clear the cache before restoring if you have already restored a same-named version:

```powershell
Remove-Item -Recurse -Force "$env:USERPROFILE\.nuget\packages\brusca.core" -ErrorAction SilentlyContinue
```

---

## 3. Apply the PII schema

Run the SQL in `Brusca.Core/docs/DeveloperGuide.md` §5 against your `BruscaDb` database to create the three new tables:

- `cleaning.RedactedFile`
- `cleaning.StructurePlan`
- `cleaning.FileRelocation`

…and the matching `usp_RedactedFile_*`, `usp_StructurePlan_*`, `usp_FileRelocation_*` stored procedures. Templates for those procedures sit alongside the existing `cleaning.usp_Cleaning_*` set in your migration project.

---

## 4. Restore + build

```powershell
cd \\OOBT-NAS\Workstation\Repo\Brusca.Infrastructure
dotnet restore Brusca.Infrastructure/Brusca.Infrastructure.csproj --force
dotnet build   Brusca.Infrastructure/Brusca.Infrastructure.csproj -c Debug
```

---

## 5. Pack to the local feed

```powershell
dotnet pack Brusca.Infrastructure/Brusca.Infrastructure.csproj -c Debug -o ..\nupkgs /p:Version=1.0.999-pii
```

`Brusca.Api` and `Brusca.Tests` will pick this version up after their next restore.

---

## 6. Encryption keys (Data Protection)

The PII column is sealed with ASP.NET Core Data Protection. By default keys are stored in:

- **Windows**: `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` (DPAPI-protected per user).
- **Linux**: an in-process key (you MUST configure a key-ring path).

To override the key-ring location:

```json
"Brusca": {
  "Pii": {
    "DataProtectionApplicationName": "Brusca.Pii",
    "KeyRingDirectory": "C:\\ProgramData\\Brusca\\keys"
  }
}
```

For multi-instance deployments persist the keys to a shared store such as Azure Key Vault, AWS S3, or a SQL key store and update `InfrastructureRegistration.cs` accordingly.

---

## 7. Smoke check

```powershell
$pkg = Get-ChildItem ..\nupkgs\Brusca.Infrastructure.*.nupkg | Sort-Object LastWriteTime -Desc | Select -First 1
$tmp = Join-Path $env:TEMP "binfra_$(New-Guid)"
Expand-Archive $pkg.FullName $tmp -Force
[System.Reflection.Assembly]::LoadFrom("$tmp\lib\net9.0\Brusca.Infrastructure.dll").GetTypes() |
  Where-Object { $_.Name -match 'Pii|Encryption|Structure|Redacted|Relocation' } |
  Select FullName
```

Expected types include `RegexPiiRedactionService`, `HeuristicDocumentTypeClassifier`, `DataProtectionEncryptionService`, `ClaudeStructureService`, `StructureExecutionService`, `RedactedFileRepository`, `StructurePlanRepository`, `FileRelocationRepository`.
