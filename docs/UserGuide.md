# User Guide — Brusca.Infrastructure

`Brusca.Infrastructure` is consumed via DI registration only — no end-user surface. This guide is for service developers who need to understand what each implementation does.

---

## 1. Service catalogue

| Interface | Implementation | Notes |
|-----------|----------------|-------|
| `ICleaningService`        | `Services/CleaningService.cs`         | Orchestrates the entire pipeline. |
| `IFileSystemService`      | `Services/FileSystemService.cs`       | Walks UNC + local paths. |
| `IFileExtensionService`   | `Services/FileExtensionService.cs`    | Manages master extension list. |
| `ITreeProjectionService`  | `Services/TreeProjectionService.cs`   | Computes "after" tree from approved steps. |
| `IPiiRedactionService`    | `Pii/RegexPiiRedactionService.cs`     | Regex-based PII detector with stable `[[PII:Kind:NNNN]]` tokens. |
| `IDocumentTypeClassifier` | `Pii/HeuristicDocumentTypeClassifier.cs` | Extension-first; keyword fallback. |
| `IEncryptionService`      | `Encryption/DataProtectionEncryptionService.cs` | ASP.NET Core Data Protection. |
| `IClaudeStructureService` | `Claude/ClaudeStructureService.cs`    | Anonymized Claude call (DocumentType + extension only). |
| `IStructureExecutionService` | `Services/StructureExecutionService.cs` | Applies plan, records `FileRelocationRecord`. |
| `IErrorLogger`            | `Logging/SerilogErrorLogger.cs`       | Configurable Serilog sinks. |
| `IAuditLogger`            | `Logging/AuditNetAuditLogger.cs`      | Configurable Audit.NET providers. |

---

## 2. The PII pipeline at runtime

`CleaningService.RedactAndClassifyAsync(cleaningId, ct)` does the following per discovered file:

1. `IFileSystemService.ReadFileContentAsync` — reads file content (size-capped via `FileSystemOptions.MaxFileSizeBytes`).
2. `IPiiRedactionService.RedactAsync` — produces redacted text and a `PiiSegment[]`.
3. `IDocumentTypeClassifier.ClassifyAsync` — assigns `DocumentType` from the **redacted** text.
4. `IEncryptionService.Encrypt` — seals the JSON of `PiiSegment[]` into the column.
5. `IRedactedFileRepository.CreateAsync` — persists a `RedactedFileDescriptor`.

The descriptor that lands in the DB:

- contains the **redacted** content (PII replaced by `[[PII:Kind:NNNN]]` tokens),
- contains an **encrypted** JSON blob of the original PII values,
- contains the `DocumentType` and the file extension,
- contains the SHA-256 hash of the original file content for integrity.

`CleaningService.GenerateStructurePlanAsync(cleaningId, ct)` then:

1. Calls `IRedactedFileRepository.GetDocumentTypeSummariesAsync` — aggregates by `(DocumentType, Extension)`.
2. Sends only those summaries to `IClaudeStructureService.AnalyzeStructureAsync`.
3. Persists the resulting `DirectoryStructurePlan` via `IStructurePlanRepository`.

`CleaningService.ExecuteStructurePlanAsync(cleaningId, ct)` delegates to `IStructureExecutionService`:

1. Loads the plan + every `RedactedFileDescriptor` for the cleaning.
2. For each descriptor: matches a rule by `DocumentType` (and extension if specified).
3. **Decrypts the PII JSON in memory only** to fill `{{PersonName}}`, `{{InvoiceNumber}}`, etc., into the rule's `FolderPathTemplate` / `FileNameTemplate`.
4. Creates the target folder (recorded as `RelocationOperationType.CreateDirectory`).
5. Moves (source target) **or** copies (alternate target) the file (recorded as `Move` or `Materialize`).
6. Persists a `FileRelocationRecord` with both `BeforePath`/`BeforeName` and `AfterPath`/`AfterName`.

PII never crosses an API or logging boundary.

---

## 3. Configuration anchors

```jsonc
"Brusca": {
  "DatabaseConnectionString": "...",
  "Claude": { "ApiKey": "...", "Model": "claude-opus-4-5", "MaxTokens": 4096 },
  "FileSystem": { ... },
  "Pii": {
    "Enabled": true,
    "DataProtectionApplicationName": "Brusca.Pii",
    "KeyRingDirectory": null,
    "MaxRedactedContentChars": 4000,
    "Detectors": { /* per-kind toggles */ },
    "CustomRules": [ { "Name": "EmployeeId", "RegexPattern": "EMP-\\d{6}", "Kind": "Custom" } ]
  }
}
```

---

## 4. Replacing an implementation

To swap any service:

1. Implement the corresponding `Brusca.Core.Contracts.*` interface.
2. Register your replacement after `services.AddBruscaInfrastructure(configuration)` in your composition root, e.g.:

```csharp
services.AddBruscaInfrastructure(configuration);
services.Replace(ServiceDescriptor.Scoped<IPiiRedactionService, MyMlPiiRedactionService>());
```

Common replacements:

- **PII redaction** → swap regex for a Microsoft Presidio container, an ML model, or a Claude-backed redactor.
- **Document classifier** → swap heuristics for a fine-tuned classifier or a Claude `Haiku` call (the classifier still receives only **redacted** content so this is safe).
- **Encryption** → swap Data Protection for an HSM/KMS-backed service.
