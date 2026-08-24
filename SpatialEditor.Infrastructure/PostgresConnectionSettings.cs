using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace SpatialEditor.Infrastructure;

public sealed class PostgresConnectionSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "dinno";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public bool IncludeErrorDetail { get; set; } = true;

    [JsonIgnore]
    public string ConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Database = Database,
                Username = Username,
                Password = Password,
                IncludeErrorDetail = IncludeErrorDetail,
                Timeout = 10,
                CommandTimeout = 60
            };
            return builder.ConnectionString;
        }
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "postgres-connection.json");

    public static async Task<PostgresConnectionSettings> LoadAsync(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        if (!File.Exists(path))
        {
            return new PostgresConnectionSettings();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PostgresConnectionSettings>(stream)
            ?? new PostgresConnectionSettings();
    }

    public async Task SaveAsync(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
    }
}