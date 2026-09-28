using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DipTick.Models.Protocol;

/// <summary>A proposal's body: a title, numbered clauses, and the checklist items that become boxes.</summary>
internal sealed record ProposalBody(string Title, IReadOnlyList<string> Clauses, IReadOnlyList<string> Items)
{
    /// <summary>The text a <c>where</c> names: <c>C3</c> or <c>K2</c>; null if there is none.</summary>
    public string? At(string where)
    {
        if (where.Length < 2 || !int.TryParse(where.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
        {
            return null;
        }

        var list = where[0] switch { 'C' => Clauses, 'K' => Items, _ => null };
        return list is not null && n <= list.Count ? list[n - 1] : null;
    }

    /// <summary>Whether two bodies say the same thing, clause for clause.</summary>
    public bool SameAs(ProposalBody other) =>
        Title == other.Title && Clauses.SequenceEqual(other.Clauses, StringComparer.Ordinal) && Items.SequenceEqual(other.Items, StringComparer.Ordinal);
}

/// <summary>
/// One version of a proposal, and the hash every vote is bound to.
/// </summary>
/// <remarks>
/// The canonical form is <c>{"id","v","parent","title","clauses","items"}</c> in that order,
/// strings normalised by <see cref="SafeText.ForModel"/> (NFC, LF). The hash is SHA-256 of its
/// UTF-8. The author is not in it: with the body known, the author could be found by trying
/// each seat. <c>parent</c> is the previous version's full hash, so versions chain.
/// </remarks>
internal sealed record ProposalVersion(string Id, int V, string Parent, ProposalBody Body)
{
    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    public string Hash { get; } = HashOf(Id, V, Parent, Body);

    /// <summary>How a version is shown: <c>P3 v2 #9f2c1a7e</c>.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Id} v{V} #{Hash[..8]}");

    /// <summary>Whether a reference's hash prefix points at this version.</summary>
    public bool Matches(VersionRef r) => r.P == Id && r.V == V && Hash.StartsWith(r.H, StringComparison.Ordinal);

    public static string Canonical(string id, int v, string parent, ProposalBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Compact))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteNumber("v", v);
            json.WriteString("parent", parent);
            json.WriteString("title", SafeText.ForModel(body.Title));
            json.WriteStartArray("clauses");
            foreach (var clause in body.Clauses)
            {
                json.WriteStringValue(SafeText.ForModel(clause));
            }

            json.WriteEndArray();
            json.WriteStartArray("items");
            foreach (var item in body.Items)
            {
                json.WriteStringValue(SafeText.ForModel(item));
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string HashOf(string id, int v, string parent, ProposalBody body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(id, v, parent, body))));
}
