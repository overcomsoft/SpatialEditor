using System.IO;
using System.Text.Json;

namespace SpatialEditor.App;

/// <summary>
/// User preferences of the editor (login, shortcuts, auto-save), kept in
/// %LocalAppData%\SpatialEditor\app-settings.json. The saved login ID/password stay in the
/// connection settings file; this file only holds the options.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Log in with the saved ID/password at start-up without showing the login window.</summary>
    public bool AutoLogin { get; set; }

    /// <summary>Keep the (DPAPI-encrypted) password after a successful login.</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>Shortcut overrides: command id → gesture text ("" = no shortcut). Missing ids use the default.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>Save pending edits automatically while in edit mode.</summary>
    public bool AutoSaveEnabled { get; set; }

    public int AutoSaveMinutes { get; set; } = 5;

    /// <summary>Outline color of selected objects, "#RRGGBB".</summary>
    public string SelectionColor { get; set; } = "#FFD700";

    /// <summary>Outline thickness of selected objects in screen pixels.</summary>
    public double SelectionThickness { get; set; } = 1.5;

    /// <summary>Keep every edit save as an automatic version of the drawing (kind "auto").</summary>
    public bool AutoVersionOnSave { get; set; } = true;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "app-settings.json");

    public AppSettings Clone() => new()
    {
        AutoLogin = AutoLogin,
        RememberPassword = RememberPassword,
        Shortcuts = new Dictionary<string, string>(Shortcuts),
        AutoSaveEnabled = AutoSaveEnabled,
        AutoSaveMinutes = AutoSaveMinutes,
        SelectionColor = SelectionColor,
        SelectionThickness = SelectionThickness,
        AutoVersionOnSave = AutoVersionOnSave
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.AutoSaveMinutes = Math.Clamp(loaded.AutoSaveMinutes, 1, 120);
                    loaded.SelectionThickness = Math.Clamp(loaded.SelectionThickness, 0.5, 8);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // A damaged settings file falls back to the defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
