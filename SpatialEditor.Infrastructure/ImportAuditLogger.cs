namespace SpatialEditor.Infrastructure;

public sealed class ImportAuditLogger
{
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "logs");

    public void Write(string message)
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, $"import-{DateTime.Now:yyyyMMdd}.log");
        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
}