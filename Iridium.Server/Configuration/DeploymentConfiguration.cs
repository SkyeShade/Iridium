using Microsoft.Data.Sqlite;

namespace Iridium.Server.Configuration;

public static class DeploymentConfiguration
{
    public const string DataDirectoryEnvironmentVariable = "IRIDIUM_DATA_DIR";
    public const string ConfigDirectoryEnvironmentVariable = "IRIDIUM_CONFIG_DIR";

    public static void AddExternalConfiguration(ConfigurationManager configuration, IHostEnvironment environment)
    {
        var configDirectory = Environment.GetEnvironmentVariable(ConfigDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            var fullConfigDirectory = Path.GetFullPath(configDirectory);
            configuration.AddJsonFile(Path.Combine(fullConfigDirectory, "appsettings.json"), optional: true,
                reloadOnChange: true);
            configuration.AddJsonFile(
                Path.Combine(fullConfigDirectory, $"appsettings.{environment.EnvironmentName}.json"), optional: true,
                reloadOnChange: true);

            // WebApplication.CreateBuilder has already added environment variables. Add them again so they remain
            // higher priority than the external JSON files.
            configuration.AddEnvironmentVariables();
        }

        var dataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDataDirectory);

        configuration["ConnectionStrings:Iridium"] = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullDataDirectory, "iridium.db")
        }.ToString();
        configuration[$"{NodeOptions.SectionName}:AttachmentStoragePath"] =
            Path.Combine(fullDataDirectory, "attachments");
    }
}
