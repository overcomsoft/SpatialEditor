using System.Globalization;
using System.Reflection;

namespace SpatialEditor.App;

/// <summary>Development build number: the date and time the program was built (yyyyMMddHHmmss).</summary>
internal static class BuildInfo
{
    /// <summary>The raw 14-digit stamp, e.g. 20261008153045.</summary>
    public static string Number { get; } = ReadStamp();

    /// <summary>Readable form, e.g. 2026-10-08 15:30:45.</summary>
    public static string Display { get; } =
        DateTime.TryParseExact(Number, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var built)
            ? built.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : Number;

    /// <summary>Text for the window and the About box, e.g. "Build 20261008153045".</summary>
    public static string Label => $"Build {Number}";

    private static string ReadStamp() =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildStamp")?.Value ?? "unknown";
}
