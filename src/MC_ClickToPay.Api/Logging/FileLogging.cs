using Serilog;
using Serilog.Events;

namespace MC_ClickToPay.Api.Logging;

public sealed class FileLoggingOptions
{
    public const string SectionName = "FileLogging";

    /// <summary>Folder of the daily log files (api-YYYYMMDD.log); relative to the content root. Empty = no file log.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Days of files kept; older ones are deleted.</summary>
    public int RetainedDays { get; set; } = 30;
}

/// <summary>
/// Serilog: console plus one file per day, so errors are kept on the server (IIS discards the console), including the
/// ones the database cannot hold: startup failures, migrations, an unreachable database. Levels can be changed in the
/// "Serilog" section. Microsoft.AspNetCore stays at Warning: its request logs carry the path, which holds the SPI token
/// of /api/confirmations/{spiToken}; requests are logged by <see cref="ApiRequestLogMiddleware"/> with the route
/// template instead.
/// </summary>
public static class FileLogging
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static void Configure(LoggerConfiguration logger, IConfiguration configuration, IHostEnvironment environment,
        IServiceProvider services)
    {
        var options = configuration.GetSection(FileLoggingOptions.SectionName).Get<FileLoggingOptions>() ?? new();
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: Template, formatProvider: System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(options.Directory))
        {
            var directory = Path.GetFullPath(options.Directory, environment.ContentRootPath);
            logger.WriteTo.File(Path.Combine(directory, "api-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, options.RetainedDays),
                shared: true,
                outputTemplate: Template,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
