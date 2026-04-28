using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Pii;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Regex-based PII redactor. Replaces detected spans with stable tokens of
/// the form <c>[[PII:{Kind}:{Ordinal:0000}]]</c>.
///
/// This implementation deliberately favours precision over recall — it will
/// not detect free-form PII (e.g. unstructured names) but it WILL find every
/// pattern matching a configured detector. Custom rules can extend coverage.
/// </summary>
public sealed partial class RegexPiiRedactionService : IPiiRedactionService
{
    private readonly PiiOptions _opts;
    private readonly IReadOnlyList<(PiiKind Kind, Regex Pattern, string Label)> _detectors;

    public RegexPiiRedactionService(IOptions<BruscaOptions> options)
    {
        _opts = options.Value.Pii;
        _detectors = BuildDetectors(_opts);
    }

    public Task<Result<PiiRedactionResult>> RedactAsync(
        string content, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(content))
            return Task.FromResult(Result.Ok(new PiiRedactionResult
            {
                RedactedContent = string.Empty,
                Segments = []
            }));

        var matches = new List<(int Start, int Length, PiiKind Kind, string Value, string Label)>();

        foreach (var (kind, pattern, label) in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            foreach (Match m in pattern.Matches(content))
            {
                if (!m.Success || m.Length == 0) continue;
                matches.Add((m.Index, m.Length, kind, m.Value, label));
            }
        }

        // Resolve overlaps — keep the earliest, longest match (greedy left-to-right).
        var resolved = ResolveOverlaps(matches);

        var sb = new StringBuilder(content.Length);
        var segments = new List<PiiSegment>(resolved.Count);
        int cursor = 0, ordinal = 0;

        foreach (var m in resolved.OrderBy(x => x.Start))
        {
            if (m.Start > cursor) sb.Append(content, cursor, m.Start - cursor);

            ordinal++;
            var token = $"[[PII:{m.Kind}:{ordinal:D4}]]";
            sb.Append(token);
            segments.Add(new PiiSegment
            {
                Ordinal    = ordinal,
                Kind       = m.Kind,
                Value      = m.Value,
                Token      = token,
                StartIndex = m.Start,
                Length     = m.Length,
                Label      = m.Label
            });
            cursor = m.Start + m.Length;
        }
        if (cursor < content.Length) sb.Append(content, cursor, content.Length - cursor);

        var redacted = sb.ToString();
        if (_opts.MaxRedactedContentChars > 0 && redacted.Length > _opts.MaxRedactedContentChars)
            redacted = redacted[.._opts.MaxRedactedContentChars];

        return Task.FromResult(Result.Ok(new PiiRedactionResult
        {
            RedactedContent = redacted,
            Segments = segments
        }));
    }

    /// <summary>SHA-256 hex hash of the original content — for integrity tracking.</summary>
    public static string Sha256Hex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    // ── Detectors ────────────────────────────────────────────────────────────

    private static List<(PiiKind, Regex, string)> BuildDetectors(PiiOptions opts)
    {
        var list = new List<(PiiKind, Regex, string)>();
        var t = opts.Detectors;

        if (t.EmailAddress)
            list.Add((PiiKind.EmailAddress, EmailRegex(), nameof(PiiKind.EmailAddress)));
        if (t.SocialSecurityNumber)
            list.Add((PiiKind.SocialSecurityNumber, SsnRegex(), nameof(PiiKind.SocialSecurityNumber)));
        if (t.CreditCardNumber)
            list.Add((PiiKind.CreditCardNumber, CreditCardRegex(), nameof(PiiKind.CreditCardNumber)));
        if (t.PhoneNumber)
            list.Add((PiiKind.PhoneNumber, PhoneRegex(), nameof(PiiKind.PhoneNumber)));
        if (t.IpAddress)
            list.Add((PiiKind.IpAddress, IpV4Regex(), nameof(PiiKind.IpAddress)));
        if (t.DateOfBirth)
            list.Add((PiiKind.DateOfBirth, DobRegex(), nameof(PiiKind.DateOfBirth)));
        if (t.StreetAddress)
            list.Add((PiiKind.StreetAddress, StreetAddressRegex(), nameof(PiiKind.StreetAddress)));
        if (t.BankAccountNumber)
            list.Add((PiiKind.BankAccountNumber, BankAccountRegex(), nameof(PiiKind.BankAccountNumber)));
        if (t.PassportNumber)
            list.Add((PiiKind.PassportNumber, PassportRegex(), nameof(PiiKind.PassportNumber)));
        if (t.DriversLicense)
            list.Add((PiiKind.DriversLicense, DriversLicenseRegex(), nameof(PiiKind.DriversLicense)));
        if (t.TaxId)
            list.Add((PiiKind.TaxId, EinRegex(), nameof(PiiKind.TaxId)));
        if (t.VehicleIdentificationNumber)
            list.Add((PiiKind.VehicleIdentificationNumber, VinRegex(), nameof(PiiKind.VehicleIdentificationNumber)));
        if (t.MedicalRecordNumber)
            list.Add((PiiKind.MedicalRecordNumber, MrnRegex(), nameof(PiiKind.MedicalRecordNumber)));
        if (t.PersonName)
            list.Add((PiiKind.PersonName, PersonNameRegex(), nameof(PiiKind.PersonName)));

        foreach (var rule in opts.CustomRules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.RegexPattern)) continue;
            if (!Enum.TryParse<PiiKind>(rule.Kind, true, out var kind)) kind = PiiKind.Custom;
            list.Add((kind,
                new Regex(rule.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
                rule.Name));
        }

        return list;
    }

    private static List<(int Start, int Length, PiiKind Kind, string Value, string Label)>
        ResolveOverlaps(List<(int Start, int Length, PiiKind Kind, string Value, string Label)> all)
    {
        var sorted = all
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.Length)
            .ToList();

        var result = new List<(int, int, PiiKind, string, string)>();
        int lastEnd = -1;
        foreach (var m in sorted)
        {
            if (m.Start < lastEnd) continue; // overlap — keep the earlier/longer one
            result.Add(m);
            lastEnd = m.Start + m.Length;
        }
        return result;
    }

    // ── Generated regexes ────────────────────────────────────────────────────

    [GeneratedRegex(@"\b[\w.+\-]{1,64}@[A-Za-z0-9.\-]{2,253}\.[A-Za-z]{2,24}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}[- ]?(?!00)\d{2}[- ]?(?!0000)\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"\b(?:\+?1[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled)]
    private static partial Regex IpV4Regex();

    [GeneratedRegex(@"\b(?:0?[1-9]|1[0-2])[/\-.](?:0?[1-9]|[12]\d|3[01])[/\-.](?:19|20)\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex DobRegex();

    [GeneratedRegex(@"\b\d{1,5}\s+[A-Z][A-Za-z\.]+(?:\s+[A-Z][A-Za-z\.]+){0,4}\s+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln|Court|Ct|Way|Place|Pl|Terrace|Ter|Circle|Cir)\b\.?", RegexOptions.Compiled)]
    private static partial Regex StreetAddressRegex();

    [GeneratedRegex(@"\b\d{8,17}\b", RegexOptions.Compiled)]
    private static partial Regex BankAccountRegex();

    [GeneratedRegex(@"\b[A-PR-WY][1-9]\d\s?\d{4}[1-9]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PassportRegex();

    [GeneratedRegex(@"\b[A-Z]\d{7,12}\b", RegexOptions.Compiled)]
    private static partial Regex DriversLicenseRegex();

    [GeneratedRegex(@"\b\d{2}-\d{7}\b", RegexOptions.Compiled)]
    private static partial Regex EinRegex();

    [GeneratedRegex(@"\b[A-HJ-NPR-Z0-9]{17}\b", RegexOptions.Compiled)]
    private static partial Regex VinRegex();

    [GeneratedRegex(@"\bMRN[:\s]*\d{6,12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MrnRegex();

    /// <summary>
    /// Conservative person-name matcher: two or three TitleCase words.
    /// Likely to over-match — that is acceptable: false positives stay encrypted.
    /// </summary>
    [GeneratedRegex(@"\b(?:Mr|Mrs|Ms|Dr|Prof)\.?\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+){0,2}\b", RegexOptions.Compiled)]
    private static partial Regex PersonNameRegex();
}
