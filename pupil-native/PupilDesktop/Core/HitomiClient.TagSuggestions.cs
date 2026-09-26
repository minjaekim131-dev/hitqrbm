using System.Text.Json;

namespace PupilDesktop.Core;

public sealed partial class HitomiClient
{
    public async Task<List<string>> GetTagSuggestionsAsync(string input, CancellationToken ct = default)
    {
        var text = input.Trim().ToLowerInvariant().Replace('_', ' ');
        if (string.IsNullOrWhiteSpace(text)) return [];

        var negative = text.StartsWith('-');
        if (negative) text = text[1..];

        var type = "global";
        var term = text;
        var idx = text.IndexOf(':');
        if (idx > 0)
        {
            var requested = text[..idx].Trim();
            if (requested is "tag" or "female" or "male")
            {
                type = requested;
                term = text[(idx + 1)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(term)) return [];

        var path = BuildSuggestionPath(type, term);
        var url = $"https://{TagIndexDomain}{path}.json";

        using var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var output = new List<(string Query, int Count)>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;
            var name = row[0].GetString()?.Trim();
            var count = row[1].ValueKind == JsonValueKind.Number && row[1].TryGetInt32(out var n) ? n : 0;
            var rowType = row[2].GetString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || rowType is not ("tag" or "female" or "male")) continue;

            var query = $"{rowType}:{name.Replace(' ', '_')}";
            if (negative) query = "-" + query;
            output.Add((query, count));
        }

        return output
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Query, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Query)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private static string BuildSuggestionPath(string type, string term)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('/').Append(type);
        foreach (var c in term)
        {
            sb.Append('/');
            sb.Append(c switch
            {
                '.' => "dot",
                '/' => "slash",
                _ => c.ToString()
            });
        }
        return sb.ToString();
    }
}
