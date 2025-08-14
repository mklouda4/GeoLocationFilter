using MaxMind.GeoIP2;
using Prometheus;
using System.Net;

namespace GeoLocationFilter.Services
{
    public interface IGeoLocationService
    {
        Task<string?> GetCountryCodeAsync(string ipAddress);
        bool IsReady { get; }
    }

    public class MaxMindGeoLocationService : IGeoLocationService, IDisposable
    {
        private readonly ILogger<MaxMindGeoLocationService> _logger;
        private readonly IConfiguration _configuration;
        private DatabaseReader? _reader;
        private FileSystemWatcher? _fileWatcher;
        private readonly object _lock = new();
        private string? _databasePath;
        private DateTime _lastLoadTime;

        // Metrics
        private static readonly Counter MaxMindLookups = Metrics
            .CreateCounter("geoguard_maxmind_lookups_total", "MaxMind database lookups", "result");

        private static readonly Gauge DatabaseLoadTime = Metrics
            .CreateGauge("geoguard_maxmind_database_load_timestamp", "Last database load time");

        public bool IsReady => _reader != null;

        public MaxMindGeoLocationService(ILogger<MaxMindGeoLocationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _databasePath = _configuration["DbPath"];

            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await LoadDatabaseAsync();
            SetupFileWatcher();
        }

        private async Task LoadDatabaseAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_databasePath) || !File.Exists(_databasePath))
                {
                    _logger.LogWarning("MaxMind database not found at path: {DatabasePath}", _databasePath);
                    return;
                }

                lock (_lock)
                {
                    _reader?.Dispose();
                    _reader = new DatabaseReader(_databasePath);
                    _lastLoadTime = DateTime.UtcNow;
                }

                DatabaseLoadTime.SetToCurrentTimeUtc();

                _logger.LogInformation("MaxMind database loaded successfully from {DatabasePath}", _databasePath);
                _logger.LogInformation("Database metadata: {Metadata}",
                    $"Type: {_reader.Metadata.DatabaseType}, Build: {_reader.Metadata.BuildDate}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load MaxMind database from {DatabasePath}", _databasePath);
                lock (_lock)
                {
                    _reader?.Dispose();
                    _reader = null;
                }
            }
        }

        private void SetupFileWatcher()
        {
            if (string.IsNullOrEmpty(_databasePath))
                return;

            try
            {
                var directory = Path.GetDirectoryName(_databasePath);
                var filename = Path.GetFileName(_databasePath);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    _logger.LogWarning("Cannot watch directory {Directory} - does not exist", directory);
                    return;
                }

                _fileWatcher?.Dispose();
                _fileWatcher = new FileSystemWatcher(directory, filename)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnDatabaseFileChanged;

                _logger.LogInformation("File watcher setup for MaxMind database: {DatabasePath}", _databasePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to setup file watcher for {DatabasePath}", _databasePath);
            }
        }

        private async void OnDatabaseFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                _logger.LogInformation("MaxMind database file changed, reloading...");
                await Task.Delay(5000);
                await LoadDatabaseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during database reload");
            }
        }

        public async Task<string?> GetCountryCodeAsync(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                MaxMindLookups.WithLabels("invalid_ip").Inc();
                return null;
            }

            DatabaseReader? reader;
            lock (_lock)
            {
                reader = _reader;
            }

            await Task.CompletedTask;

            if (reader == null)
            {
                MaxMindLookups.WithLabels("no_database").Inc();
                return null;
            }

            try
            {
                if (reader.TryCountry(ip, out var response) && response != null)
                {
                    var countryCode = response.Country.IsoCode;
                    MaxMindLookups.WithLabels("success").Inc();
                    return countryCode;
                }
                else
                {
                    MaxMindLookups.WithLabels("not_found").Inc();
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up country for IP {IpAddress}", ipAddress);
                MaxMindLookups.WithLabels("error").Inc();
                return null;
            }
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
            lock (_lock)
            {
                _reader?.Dispose();
            }
        }
    }
}