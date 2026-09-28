using System.Text.Json;

namespace DipTick.Models;

/// <summary>One tab as it was left: its kept chat (null for one nobody had spoken in), its unsent draft, and how far down it was scrolled (0 to 1).</summary>
internal sealed record TabLayout(string? Session, string Draft, double Scroll);

/// <summary>One window as it was left: its tabs, left to right, and which was in front.</summary>
internal sealed record WindowLayout(IReadOnlyList<TabLayout> Tabs, int Current);

/// <summary>
/// What the window remembers for the person between runs, in <c>ui.json</c> in DipTick's data
/// folder: per viewer, never a setting, so it lives beside the sessions and not in
/// <c>diptick.master.json</c>. It holds whether setup has already opened by itself once (it opens
/// by itself at first use, then only from Settings), the drafts left unsent at the last quit, by
/// chat, and the windows and tabs left open and the rail's folded sections. Never a private tab.
/// </summary>
/// <remarks>
/// A file that is missing, unreadable or not what this build wrote reads as nothing remembered;
/// reading never throws. Writing goes beside the file first and is then moved into place, so a
/// crash never leaves half a file.
/// </remarks>
internal sealed class UiState
{
    public const string FileName = "ui.json";

    private readonly string _path;
    private Dictionary<string, string> _drafts;
    private List<WindowLayout> _windows;
    private SortedSet<string> _folded;

    private UiState(string path, bool setupShown, Dictionary<string, string> drafts, List<WindowLayout>? windows = null, SortedSet<string>? folded = null)
    {
        _path = path;
        SetupShown = setupShown;
        _drafts = drafts;
        _windows = windows ?? [];
        _folded = folded ?? new SortedSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Whether setup has opened by itself before; after that it opens only from Settings.</summary>
    public bool SetupShown { get; private set; }

    /// <summary>The drafts left unsent at the last quit, by chat id; "" is the chat nobody had spoken in.</summary>
    public IReadOnlyDictionary<string, string> Drafts => _drafts;

    /// <summary>The windows and tabs left open at the last quit, the first window first; empty when none were remembered.</summary>
    public IReadOnlyList<WindowLayout> Windows => _windows;

    /// <summary>The rail's sections the person folded, by name.</summary>
    public IReadOnlyCollection<string> Folded => _folded;

    /// <summary>The state in <paramref name="folder"/> (DipTick's data folder), or nothing remembered.</summary>
    public static UiState Load(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return new UiState(path, setupShown: false, []);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new UiState(path, setupShown: false, []);
            }

            var shown = root.TryGetProperty("setupShown", out var value) && value.ValueKind == JsonValueKind.True;
            var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("drafts", out var kept) && kept.ValueKind == JsonValueKind.Object)
            {
                foreach (var draft in kept.EnumerateObject())
                {
                    if (draft.Value.ValueKind == JsonValueKind.String && draft.Value.GetString() is { Length: > 0 } text)
                    {
                        drafts[draft.Name] = text;
                    }
                }
            }

            var windows = new List<WindowLayout>();
            if (root.TryGetProperty("windows", out var left) && left.ValueKind == JsonValueKind.Array)
            {
                foreach (var window in left.EnumerateArray().Where(w => w.ValueKind == JsonValueKind.Object))
                {
                    var tabs = new List<TabLayout>();
                    if (window.TryGetProperty("tabs", out var open) && open.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tab in open.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
                        {
                            var session = tab.TryGetProperty("session", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                            var draft = tab.TryGetProperty("draft", out var typed) && typed.ValueKind == JsonValueKind.String ? typed.GetString() ?? string.Empty : string.Empty;
                            var scroll = tab.TryGetProperty("scroll", out var down) && down.ValueKind == JsonValueKind.Number ? Math.Clamp(down.GetDouble(), 0, 1) : 1;
                            tabs.Add(new TabLayout(session, draft, scroll));
                        }
                    }

                    var current = window.TryGetProperty("current", out var front) && front.ValueKind == JsonValueKind.Number && front.TryGetInt32(out var index) ? index : 0;
                    if (tabs.Count > 0)
                    {
                        windows.Add(new WindowLayout(tabs, Math.Clamp(current, 0, tabs.Count - 1)));
                    }
                }
            }

            var folded = new SortedSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("folded", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in sections.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String))
                {
                    folded.Add(section.GetString()!);
                }
            }

            return new UiState(path, shown, drafts, windows, folded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiState(path, setupShown: false, []);
        }
    }

    /// <summary>Remembers that setup has opened by itself. Returns why it could not be written, or null.</summary>
    public string? MarkSetupShown()
    {
        SetupShown = true;
        return Write() is { } why ? $"{why}; setup may open by itself again next time." : null;
    }

    /// <summary>
    /// Remembers the drafts left unsent at the quit of a chat on its own (the terminal screen), by
    /// chat id, in place of those remembered before; no window was left open, so none comes back
    /// next time. Empty drafts are not kept. Returns why they could not be written, or null.
    /// </summary>
    public string? KeepDrafts(IReadOnlyDictionary<string, string> drafts) => KeepQuit(drafts, []);

    /// <summary>
    /// Remembers, at quit, the drafts left unsent and the windows and tabs left open, in place of those
    /// remembered before. The caller leaves every private tab out. Returns why they could not be
    /// written, or null.
    /// </summary>
    public string? KeepQuit(IReadOnlyDictionary<string, string> drafts, IReadOnlyList<WindowLayout> windows)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(windows);
        _drafts = drafts.Where(d => d.Value.Trim().Length > 0).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
        _windows = [.. windows.Where(w => w.Tabs.Count > 0)];
        return Write() is { } why ? (_windows.Count == 0 ? $"{why}; the unsent drafts are not kept." : $"{why}; the unsent drafts and the open tabs are not kept.") : null;
    }

    /// <summary>Remembers that a rail section is folded, or open again. Returns why it could not be written, or null.</summary>
    public string? Fold(string section, bool folded)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (folded ? !_folded.Add(section) : !_folded.Remove(section))
        {
            return null;
        }

        return Write();
    }

    private string? Write()
    {
        var temporary = _path + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = File.Create(temporary))
            using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteBoolean("setupShown", SetupShown);
                if (_drafts.Count > 0)
                {
                    json.WriteStartObject("drafts");
                    foreach (var (chat, text) in _drafts.OrderBy(d => d.Key, StringComparer.Ordinal))
                    {
                        json.WriteString(chat, text);
                    }

                    json.WriteEndObject();
                }

                if (_windows.Count > 0)
                {
                    json.WriteStartArray("windows");
                    foreach (var window in _windows)
                    {
                        json.WriteStartObject();
                        json.WriteNumber("current", window.Current);
                        json.WriteStartArray("tabs");
                        foreach (var tab in window.Tabs)
                        {
                            json.WriteStartObject();
                            if (tab.Session is { } session)
                            {
                                json.WriteString("session", session);
                            }

                            if (tab.Draft.Length > 0)
                            {
                                json.WriteString("draft", tab.Draft);
                            }

                            json.WriteNumber("scroll", Math.Round(tab.Scroll, 3));
                            json.WriteEndObject();
                        }

                        json.WriteEndArray();
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                }

                if (_folded.Count > 0)
                {
                    json.WriteStartArray("folded");
                    foreach (var section in _folded)
                    {
                        json.WriteStringValue(section);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            File.Move(temporary, _path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The half-written file stays beside ui.json; the next write replaces it.
            }

            return $"{_path}: cannot be written ({ex.Message})";
        }
    }
}
