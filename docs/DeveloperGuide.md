# Developer Guide — Brusca.Infrastructure

For engineers building or replacing infrastructure services.

---

## 1. Repository layout

```
Brusca.Infrastructure/
├── Claude/
│   ├── ClaudePromptService.cs       (legacy step-based prompts)
│   └── ClaudeStructureService.cs    (anonymized structure planner)
├── Configuration/
│   └── InfrastructureRegistration.cs
├── Data/
│   └── Repositories/
│       ├── DapperRepositoryBase.cs
│       ├── CleaningRepository.cs
│       ├── FileExtensionRepository.cs
│       ├── PromptStepRepository.cs
│       ├── PromptStepCommandRepository.cs
│       ├── RedactedFileRepository.cs       (NEW)
│       ├── StructurePlanRepository.cs      (NEW)
│       └── FileRelocationRepository.cs     (NEW)
├── Encryption/
│   └── DataProtectionEncryptionService.cs  (NEW)
├── Logging/
│   ├── LoggingRegistration.cs
│   ├── SerilogErrorLogger.cs
│   └── AuditNetAuditLogger.cs
├── Pii/
│   ├── RegexPiiRedactionService.cs         (NEW)
│   └── HeuristicDocumentTypeClassifier.cs  (NEW)
└── Services/
    ├── CleaningService.cs                  (PIPELINE ROOT)
    ├── FileExtensionService.cs
    ├── FileSystemService.cs
    ├── TreeProjectionService.cs
    └── StructureExecutionService.cs        (NEW)
```

---

## 2. Adding a new PII detector

Edit `Pii/RegexPiiRedactionService.cs`:

```csharp
[GeneratedRegex(@"YOUR-PATTERN", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
private static partial Regex MyRegex();
```

Then add it to `BuildDetectors`:

```csharp
if (t.MyToggle)
    list.Add((PiiKind.Custom, MyRegex(), "MyDetector"));
```

For runtime-configurable patterns prefer the `PiiOptions.CustomRules` path — no recompile needed.

---

## 3. Token templates

`StructureExecutionService` substitutes `{{TokenName}}` markers in the rule templates with values from a built-in dictionary plus the decrypted PII segments. Built-in tokens:

| Token | Source |
|-------|--------|
| `{{Year}}`, `{{Month}}`, `{{Day}}`, `{{Date}}` | `DiscoveredAtUtc` |
| `{{Extension}}` | descriptor extension |
| `{{DocumentType}}` | descriptor type |
| `{{PersonName}}`, `{{EmailAddress}}`, etc. | first PII segment of that kind |

To add a new token slot, extend `BuildTokenMap` in `StructureExecutionService.cs`. Anything not present in the map is replaced with `_` so a partial decryption never produces a malformed path.

---

## 4. Audit + error logging

- All operations write an audit row via `IAuditLogger.LogAsync(eventType, entityType, entityId, userId, action, oldValues, newValues, ct)`.
- Failures inside the structure executor write to `IErrorLogger.LogErrorAsync` BUT continue processing — one bad file never aborts the whole run.

Audit event types added by the PII pipeline:

| EventType                 | When fired |
|---------------------------|------------|
| `CleaningRedacted`        | After `RedactAndClassifyAsync` completes. |
| `StructurePlanGenerated`  | After Claude responds and the plan is persisted. |
| `StructureExecuted`       | After `StructureExecutionService.ExecuteStructureAsync` completes. |

---

## 5. Testing

Unit tests live in `Brusca.Tests/Infrastructure/`:

- `RegexPiiRedactionServiceTests`        — regex coverage + token stability.
- `HeuristicDocumentTypeClassifierTests` — extension and keyword classification.
- `DataProtectionEncryptionServiceTests` — round-trip encryption.

Run with:

```powershell
cd \\OOBT-NAS\Workstation\Repo\Brusca.Tests
dotnet test
```

---

## 6. Packing & versioning

Pack from the repo root:

```powershell
dotnet pack Brusca.Infrastructure/Brusca.Infrastructure.csproj -c Debug -o ..\nupkgs /p:Version=<version>
```

If you change the public surface, bump the **minor** number; if you change the internal contracts (e.g. an interface added in `Brusca.Core`), bump the **patch** number on both packages and re-pin consumers.
