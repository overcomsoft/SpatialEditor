using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace SpatialEditor.Infrastructure;

/// <summary>A layer's display style: <see cref="Color"/> as "#RRGGBB" and line <see cref="Thickness"/> in screen pixels.</summary>
public sealed class LayerStyleSetting
{
    public string? Color { get; set; }
    public double Thickness { get; set; } = 1.0;
    /// <summary>Polygon fill "#RRGGBB"; null means the fill follows <see cref="Color"/>.</summary>
    public string? FillColor { get; set; }
    /// <summary>Polygon fill opacity, 0 (transparent) to 1 (solid).</summary>
    public double FillOpacity { get; set; } = 60.0 / 255.0;
}

public sealed class PostgresConnectionSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "dinno";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public bool IncludeErrorDetail { get; set; } = true;
    public List<string> SelectedLayers { get; set; } = new();

    /// <summary>The ID typed at the last successful login (never the password).</summary>
    public string LastLoginId { get; set; } = string.Empty;

    /// <summary>The login password, encrypted with Windows DPAPI (current user) and Base64-encoded. Empty when not remembered.</summary>
    public string LastLoginPasswordProtected { get; set; } = string.Empty;

    /// <summary>Decrypted last login password; empty if none or if it cannot be decrypted on this account/machine.</summary>
    [JsonIgnore]
    public string LastLoginPassword
    {
        get
        {
            if (string.IsNullOrEmpty(LastLoginPasswordProtected))
            {
                return string.Empty;
            }

            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(LastLoginPasswordProtected), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
        set
        {
            LastLoginPasswordProtected = string.IsNullOrEmpty(value)
                ? string.Empty
                : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(value), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
    }

    /// <summary>Per-layer line color/thickness chosen in the layer style dialog, keyed by layer name.</summary>
    public Dictionary<string, LayerStyleSetting> LayerStyles { get; set; } = new();

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