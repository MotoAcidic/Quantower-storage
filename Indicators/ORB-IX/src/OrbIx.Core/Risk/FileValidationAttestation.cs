using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OrbIx.Core.Risk;

/// <summary>
/// Reads §13's validation record from a file.
///
/// The file format, one entry per playbook / session / product combination:
///
/// <code>
/// {
///   "attestations": [
///     { "playbook": "P1", "session": "RTH", "symbol": "NQ",
///       "outOfSampleTrades": 74, "armedForwardSessions": 22,
///       "executionRealismChecked": true, "propRuleSimulationPassed": true }
///   ]
/// }
/// </code>
///
/// Two properties are the whole point.
///
/// An absent file means nothing has been validated, and that is reported as
/// <see cref="ValidationEvidence.None"/> — which the gate reads as "refuse Auto". The
/// alternative, treating an absent file as no objection, would let unattended trading start
/// because a file was missing.
///
/// A malformed file is also nothing validated, not partially validated. A record that
/// cannot be parsed is a record that cannot be trusted, and half-reading it would attest to
/// evidence nobody wrote down.
/// </summary>
public sealed class FileValidationAttestation : IValidationAttestation
{
    private readonly Dictionary<string, ValidationEvidence> attestations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">Where the record lives. Its absence is a valid, meaningful state.</param>
    /// <param name="report">
    /// Receives a line describing what was loaded or why it was not. Required: a validation
    /// record that silently failed to load is the one thing this type must never do quietly.
    /// </param>
    public FileValidationAttestation(string path, Action<string, LoggingLevel> report)
    {
        if (report is null)
            throw new ArgumentNullException(nameof(report));

        this.Path = path;

        if (string.IsNullOrWhiteSpace(path))
        {
            this.Status = "No validation record configured, so nothing is attested and Auto is refused.";
            report(this.Status, LoggingLevel.Info);
            return;
        }

        if (!File.Exists(path))
        {
            this.Status =
                $"No validation record at {path}. Nothing is attested, so Auto is refused — "
                + "which is the correct reading of an absent record, not a failure.";
            report(this.Status, LoggingLevel.Info);
            return;
        }

        try
        {
            this.Status = this.Load(path);
            report(this.Status, LoggingLevel.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this.attestations.Clear();
            this.Status =
                $"Validation record at {path} could not be read ({ex.GetType().Name}: {ex.Message}). "
                + "Treating it as nothing attested; a record that cannot be parsed cannot be trusted.";
            report(this.Status, LoggingLevel.Error);
        }
    }

    public string Path { get; }

    /// <summary>What was loaded, or why it was not. Shown to the operator.</summary>
    public string Status { get; }

    /// <summary>Combinations with a record. Empty means nothing is attested.</summary>
    public int Count => this.attestations.Count;

    private string Load(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty("attestations", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return $"Validation record at {path} has no 'attestations' array; nothing is attested.";
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var playbook = ReadString(entry, "playbook");
            var session = ReadString(entry, "session");
            var symbol = ReadString(entry, "symbol");

            if (playbook.Length == 0 || session.Length == 0 || symbol.Length == 0)
                continue;

            this.attestations[Key(playbook, session, symbol)] = new ValidationEvidence(
                ReadInt(entry, "outOfSampleTrades"),
                ReadInt(entry, "armedForwardSessions"),
                ReadBool(entry, "executionRealismChecked"),
                ReadBool(entry, "propRuleSimulationPassed"));
        }

        return $"{this.attestations.Count} validation attestations loaded from {path}.";
    }

    public ValidationEvidence For(string playbookId, string sessionName, string symbolRoot)
        => this.attestations.TryGetValue(Key(playbookId, sessionName, symbolRoot), out var evidence)
            ? evidence
            : ValidationEvidence.None;

    private static string Key(string playbook, string session, string symbol)
        => $"{playbook}|{session}|{symbol}";

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Severity for the report callback.
///
/// Declared here rather than taken from the trading platform because OrbIx.Core has no
/// reference to it — §11's rule, which is what lets this type be tested without a platform.
/// </summary>
public enum LoggingLevel
{
    Info,
    Error,
}
