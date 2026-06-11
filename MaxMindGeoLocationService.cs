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
        private static readonly TimeSpan ReloadDebounce = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OldReaderDisposeDelay = TimeSpan.FromSeconds(30);

        private readonly ILogger<MaxMindGeoLocationService> _logger;
        private readonly string _databasePath;
        private DatabaseReader? _reader;
        private FileSystemWatcher? _fileWatcher;
        private CancellationTokenSource? _reloadCts;

        // Metrics
        private static readonly Counter MaxMindLookups = Metrics
            .CreateCounter("geoguard_maxmind_lookups_total", "MaxMind database lookups", "result");

        private static readonly Gauge DatabaseLoadTime = Metrics
            .CreateGauge("geoguard_maxmind_database_load_timestamp", "Last database load time");

        public bool IsReady => Volatile.Read(ref _reader) != null;

        public MaxMindGeoLocationService(ILogger<MaxMindGeoLocationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _databasePath = configuration["DbPath"] ?? "/data/GeoLite2-Country.mmdb";

            LoadDatabase();
            SetupFileWatcher();
        }

        private void LoadDatabase()
        {
            try
            {
                if (!File.Exists(_databasePath))
                {
                    _logger.LogWarning("MaxMind database not found at path: {DatabasePath}", _databasePath);
                    return;
                }

                var newReader = new DatabaseReader(_databasePath);

                // Atomic swap; in-flight lookups may still hold the old reader,
                // so it is disposed with a delay instead of immediately.
                var oldReader = Interlocked.Exchange(ref _reader, newReader);
                DisposeLater(oldReader);

                DatabaseLoadTime.SetToCurrentTimeUtc();

                _logger.LogInformation("MaxMind database loaded successfully from {DatabasePath}", _databasePath);
                _logger.LogInformation("Database metadata: Type: {DatabaseType}, Build: {BuildDate}",
                    newReader.Metadata.DatabaseType, newReader.Metadata.BuildDate);
            }
            catch (Exception ex)
            {
                // Keep the previously loaded database (if any) so a failed reload
                // does not take the service down.
                _logger.LogError(ex, "Failed to load MaxMind database from {DatabasePath}", _databasePath);
            }
        }

        private void SetupFileWatcher()
        {
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
                _fileWatcher.Created += OnDatabaseFileChanged;
                _fileWatcher.Renamed += OnDatabaseFileChanged;

                _logger.LogInformation("File watcher setup for MaxMind database: {DatabasePath}", _databasePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to setup file watcher for {DatabasePath}", _databasePath);
            }
        }

        private void OnDatabaseFileChanged(object sender, FileSystemEventArgs e)
        {
            // A single file replacement raises several events — debounce them
            // so the database is reloaded once, after the writes settle down.
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _reloadCts, cts);
            previous?.Cancel();
            previous?.Dispose();

            _ = DebouncedReloadAsync(cts.Token);
        }

        private async Task DebouncedReloadAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(ReloadDebounce, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _logger.LogInformation("MaxMind database file changed, reloading...");
                LoadDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during database reload");
            }
        }

        public Task<string?> GetCountryCodeAsync(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                MaxMindLookups.WithLabels("invalid_ip").Inc();
                return Task.FromResult<string?>(null);
            }

            var reader = Volatile.Read(ref _reader);
            if (reader == null)
            {
                MaxMindLookups.WithLabels("no_database").Inc();
                return Task.FromResult<string?>(null);
            }

            try
            {
                if (reader.TryCountry(ip, out var response) && response != null)
                {
                    MaxMindLookups.WithLabels("success").Inc();
                    return Task.FromResult(response.Country.IsoCode);
                }

                MaxMindLookups.WithLabels("not_found").Inc();
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up country for IP {IpAddress}", ipAddress);
                MaxMindLookups.WithLabels("error").Inc();
                return Task.FromResult<string?>(null);
            }
        }

        private static void DisposeLater(IDisposable? disposable)
        {
            if (disposable == null)
                return;

            _ = Task.Delay(OldReaderDisposeDelay).ContinueWith(_ =>
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // best effort — nothing meaningful to do with a failed dispose
                }
            });
        }

        public void Dispose()
        {
            _fileWatcher?.Dispose();
            _reloadCts?.Cancel();
            _reloadCts?.Dispose();
            Interlocked.Exchange(ref _reader, null)?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
