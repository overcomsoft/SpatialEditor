using System.Windows.Input;

namespace SpatialEditor.App;

/// <summary>A key plus modifiers, written as text such as "Ctrl+Shift+S".</summary>
public readonly record struct KeyGesture2(Key Key, ModifierKeys Modifiers)
{
    public static bool TryParse(string? text, out KeyGesture2 gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Key? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var parsed) || parsed == Key.None)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture2(key.Value, modifiers);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// One program function that can be bound to a shortcut. <see cref="ShiftReverses"/> marks a command
/// where Shift (or Caps Lock) selects the opposite direction (rotate), so Shift is ignored when matching.
/// </summary>
public sealed class ShortcutCommand
{
    public ShortcutCommand(string id, string category, string name, string defaultGesture,
        Action<bool> run, Func<bool>? canExecute = null, string? menuHeader = null, bool shiftReverses = false)
    {
        Id = id;
        Category = category;
        Name = name;
        DefaultGesture = defaultGesture;
        Run = run;
        CanExecute = canExecute ?? (() => true);
        MenuHeader = menuHeader;
        ShiftReverses = shiftReverses;
        Gesture = defaultGesture;
    }

    public string Id { get; }
    public string Category { get; }
    public string Name { get; }
    public string DefaultGesture { get; }

    /// <summary>The current gesture text ("" = none).</summary>
    public string Gesture { get; set; }

    /// <summary>Runs the command; the argument is true when the reverse direction was requested.</summary>
    public Action<bool> Run { get; }

    public Func<bool> CanExecute { get; }

    /// <summary>Header(s) of the menu item(s) that show this shortcut, separated by '|'.</summary>
    public string? MenuHeader { get; }

    public bool ShiftReverses { get; }
}
