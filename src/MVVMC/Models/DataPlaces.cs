using DipTick.Models.Chat.Store;
using DipTick.Models.Harness;

namespace DipTick.Models;

/// <summary>One place DipTick keeps something: what it is, where, and what it holds.</summary>
internal sealed record DataPlace(string Label, string Path, string Help);

/// <summary>
/// Where DipTick keeps each thing (Settings, Data; <c>diptick settings</c>): one list, so the window
/// and the command line name the same places.
/// </summary>
internal static class DataPlaces
{
    /// <param name="settingsFile">The settings file in use, or where setup would write one; null when none can be.</param>
    public static IReadOnlyList<DataPlace> For(MasterConfig config, string? settingsFile)
    {
        ArgumentNullException.ThrowIfNull(config);
        var data = AuditLog.DataFolder();
        var places = new List<DataPlace>();
        if (settingsFile is not null)
        {
            places.Add(new("Settings file", settingsFile, "Every setting is kept in this file (diptick.master.json). 'diptick config list' prints them with where each came from."));
        }

        places.Add(new("Data folder", data, "Where DipTick keeps your sessions, the audit log and what the window remembers. DIPTICK_DATA_DIR moves it."));
        places.Add(new("Sessions", System.IO.Path.Combine(data, SqliteChatStore.FileName), config.Chat.Save
            ? "Every chat, kept so it can be opened again (Chats, Keep sessions)."
            : "Not written while Chats, Keep sessions is off: a session lasts as long as its window."));
        places.Add(new("Audit log", System.IO.Path.Combine(data, AuditLog.FileName), "Every decision the harness made, each line chained to the one before so a change shows. The Activity page reads it."));
        places.Add(new("Window memory", System.IO.Path.Combine(data, UiState.FileName), "What the window remembers between runs: that setup has opened once, and the drafts left unsent."));
        places.Add(new("Pastes", Chat.Pastes.Folder(data), "Long pastes from the terminal screen, kept while a composer or a kept draft names them."));
        return places;
    }
}
