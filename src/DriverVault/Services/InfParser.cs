using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DriverVault.Services;

public sealed record InfMetadata(
    string Provider,
    string DeviceClass,
    string Version,
    string DriverDate,
    DateTime? ParsedDriverDate,
    string CatalogFile);

/// <summary>
/// Parser liviano e independiente del idioma de Windows para archivos .inf de controladores.
/// </summary>
public static partial class InfParser
{
    public static InfMetadata ParseFile(string infPath)
    {
        if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
        {
            return new InfMetadata("Desconocido", "Other", "-", "-", null, string.Empty);
        }

        try
        {
            var lines = File.ReadAllLines(infPath);
            return ParseLines(lines);
        }
        catch
        {
            return new InfMetadata("Desconocido", "Other", "-", "-", null, string.Empty);
        }
    }

    public static InfMetadata ParseLines(IEnumerable<string> lines)
    {
        var versionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stringTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string currentSection = string.Empty;

        foreach (string rawLine in lines)
        {
            string line = StripComment(rawLine).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length >= 2)
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            int eqIndex = line.IndexOf('=');
            if (eqIndex <= 0) continue;

            string key = line[..eqIndex].Trim();
            string value = line[(eqIndex + 1)..].Trim().Trim('"');

            if (currentSection.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                versionValues.TryAdd(key, value);
            }
            else if (currentSection.Equals("Strings", StringComparison.OrdinalIgnoreCase))
            {
                stringTokens.TryAdd(key, value);
            }
        }

        string provider = ResolveTokens(versionValues.GetValueOrDefault("Provider", "Desconocido"), stringTokens);
        string deviceClass = ResolveTokens(versionValues.GetValueOrDefault("Class", "Other"), stringTokens);
        string catalog = ResolveTokens(versionValues.GetValueOrDefault("CatalogFile", string.Empty), stringTokens);

        string rawDriverVer = versionValues.GetValueOrDefault("DriverVer", string.Empty);
        (string formattedDate, DateTime? parsedDate, string version) = ParseDriverVer(rawDriverVer);

        return new InfMetadata(
            string.IsNullOrWhiteSpace(provider) ? "Desconocido" : provider,
            string.IsNullOrWhiteSpace(deviceClass) ? "Other" : deviceClass,
            version,
            formattedDate,
            parsedDate,
            catalog);
    }

    public static (string FormattedDate, DateTime? ParsedDate, string Version) ParseDriverVer(string rawDriverVer)
    {
        if (string.IsNullOrWhiteSpace(rawDriverVer))
        {
            return ("-", null, "-");
        }

        string[] parts = rawDriverVer.Split(',', 2, StringSplitOptions.TrimEntries);
        string datePart = parts.Length > 0 ? parts[0] : string.Empty;
        string versionPart = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "-";

        if (DateTime.TryParseExact(
                datePart,
                ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            return (parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), parsed, versionPart);
        }

        return (string.IsNullOrWhiteSpace(datePart) ? "-" : datePart, null, versionPart);
    }

    private static string ResolveTokens(string input, Dictionary<string, string> tokens)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.Contains('%'))
            return input;

        return TokenRegex().Replace(input, match =>
        {
            string tokenName = match.Groups[1].Value;
            return tokens.TryGetValue(tokenName, out string? resolved) ? resolved : match.Value;
        });
    }

    private static string StripComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex TokenRegex();
}
