using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Responses;
using Microsoft.Extensions.Options;
using System.Net;
using System.IO.Compression;
using Microsoft.Extensions.Logging;


namespace GeoIpApi
{
    public class GeoIpService : IDisposable
    {
        private DatabaseReader? _databaseReader;
        private readonly GeoIpSettings _settings;
        private readonly ILogger<GeoIpService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _decompressionTempDirectory;

        public GeoIpService(IOptions<GeoIpSettings> settings, ILogger<GeoIpService> logger, HttpClient httpClient)
        {
            _settings = settings.Value;
            _logger = logger;
            _httpClient = httpClient;
            _decompressionTempDirectory = Path.Combine(Path.GetTempPath(), "geoip_decompress_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            if (string.IsNullOrWhiteSpace(_settings.DatabasePath))
            {
                _logger.LogError("GeoIP数据库路径未配置。");
                throw new ArgumentException("GeoIP数据库路径必须配置。");
            }

            EnsureDataDirectoryExists();
            LoadDatabase();
        }

        private void EnsureDataDirectoryExists()
        {
            var dbDirectory = Path.GetDirectoryName(_settings.DatabasePath);
            if (!string.IsNullOrEmpty(dbDirectory) && !Directory.Exists(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
                _logger.LogInformation("已为GeoIP数据库创建数据目录：{Directory}", dbDirectory);
            }
        }

        private void LoadDatabase()
        {
            if (string.IsNullOrWhiteSpace(_settings.DatabasePath) || !File.Exists(_settings.DatabasePath))
            {
                _logger.LogWarning("在路径 {Path} 未找到GeoIP数据库。在更新/下载之前，查询将失败。", _settings.DatabasePath);
                return;
            }
            try
            {
                var oldReader = _databaseReader;
                _databaseReader = new DatabaseReader(_settings.DatabasePath);
                oldReader?.Dispose();
                _logger.LogInformation("GeoIP数据库已从 {Path} 成功加载。", _settings.DatabasePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 {Path} 加载GeoIP数据库失败。", _settings.DatabasePath);
            }
        }

        public CityResponse? GetGeoData(string ipAddress)
        {
            if (_databaseReader == null)
            {
                _logger.LogWarning("GeoIP数据库未加载。无法查询IP: {IpAddress}", ipAddress);
                return null;
            }

            if (!IPAddress.TryParse(ipAddress, out var parsedIpAddress))
            {
                _logger.LogWarning("无效的IP地址格式: {IpAddress}", ipAddress);
                return null;
            }

            try
            {
                return _databaseReader.City(parsedIpAddress);
            }
            catch (MaxMind.GeoIP2.Exceptions.AddressNotFoundException)
            {
                _logger.LogInformation("在GeoIP数据库中未找到IP地址: {IpAddress}", ipAddress);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询IP地址时出错: {IpAddress}", ipAddress);
                return null;
            }
        }

        public async Task UpdateDatabaseAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.DatabaseDownloadUrl) || string.IsNullOrWhiteSpace(_settings.DatabasePath))
            {
                _logger.LogWarning("数据库下载URL或路径未配置。跳过更新。");
                return;
            }

            var tempDownloadedFilePath = Path.GetTempFileName();
            string? finalDbPathInTemp = null;

            try
            {
                _logger.LogInformation("开始从 {Url} 下载GeoIP数据库。", _settings.DatabaseDownloadUrl);
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GeoIpApiUpdater/1.0 (NetCore9; AOT)");

                var response = await _httpClient.GetAsync(_settings.DatabaseDownloadUrl, cancellationToken);
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(tempDownloadedFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }
                _logger.LogInformation("GeoIP数据库已下载到临时文件: {TempPath}", tempDownloadedFilePath);

                finalDbPathInTemp = await HandleDecompression(tempDownloadedFilePath, _settings.DatabaseDownloadUrl, cancellationToken);

                if (string.IsNullOrEmpty(finalDbPathInTemp) || !File.Exists(finalDbPathInTemp))
                {
                    _logger.LogError("未能从下载内容中获取 .mmdb 文件。");
                    return;
                }

                var targetDirectory = Path.GetDirectoryName(_settings.DatabasePath);
                if (targetDirectory != null && !Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Move(finalDbPathInTemp, _settings.DatabasePath, true);
                _logger.LogInformation("GeoIP数据库已成功更新到: {Path}", _settings.DatabasePath);

                _logger.LogInformation("重新加载GeoIP数据库。");
                LoadDatabase();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "下载GeoIP数据库时发生HTTP错误。状态码: {StatusCode}。URL: {Url}。请检查许可证密钥和URL。", ex.StatusCode, _settings.DatabaseDownloadUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新GeoIP数据库时出错。");
            }
            finally
            {
                if (File.Exists(tempDownloadedFilePath)) File.Delete(tempDownloadedFilePath);
                if (Directory.Exists(_decompressionTempDirectory))
                {
                    try { Directory.Delete(_decompressionTempDirectory, true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "无法清理临时解压缩目录：{Dir}", _decompressionTempDirectory); }
                }
            }
        }

        private async Task<string?> HandleDecompression(string downloadedFilePath, string downloadUrl, CancellationToken cancellationToken)
        {
            string targetFileName = "GeoLite2-City.mmdb";
            if (_settings.DatabasePath != null)
            {
                targetFileName = Path.GetFileName(_settings.DatabasePath);
            }

            if (downloadUrl.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(downloadedFilePath).Equals(".mmdb", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("下载的文件似乎直接是 .mmdb 文件。");
                return downloadedFilePath;
            }
            else if (downloadUrl.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("正在解压缩 .gz 文件。");
                if (!Directory.Exists(_decompressionTempDirectory)) Directory.CreateDirectory(_decompressionTempDirectory);

                string decompressedFileName = Path.GetFileNameWithoutExtension(new Uri(downloadUrl).Segments.LastOrDefault(targetFileName + ".gz"));
                if (!decompressedFileName.EndsWith(".mmdb"))
                {
                    decompressedFileName = targetFileName;
                }

                var decompressedPath = Path.Combine(_decompressionTempDirectory, decompressedFileName);

                using var originalFileStream = File.OpenRead(downloadedFilePath);
                using var decompressedFileStream = File.Create(decompressedPath);
                using var decompressor = new GZipStream(originalFileStream, CompressionMode.Decompress);
                await decompressor.CopyToAsync(decompressedFileStream, cancellationToken);
                _logger.LogInformation(".gz 文件已解压缩至: {Path}", decompressedPath);
                return decompressedPath;
            }
            else if (downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("正在解压缩 .tar.gz 文件。请确保已引用 SharpCompress 或类似库（如果需要此路径）。");
                _logger.LogWarning("尝试解压缩 .tar.gz，但需要 SharpCompress 或类似库的逻辑。请实现或确保直接下载 .mmdb 文件。");
                return null;
            }
            else
            {
                _logger.LogWarning("根据URL，下载的文件类型不是 .mmdb, .gz, 或 .tar.gz。假设它是 .mmdb 文件：{Path}。如果不是，请更新逻辑。", downloadedFilePath);
                return downloadedFilePath;
            }
        }

        public void Dispose()
        {
            _databaseReader?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}