using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeoIpApi
{
    public class GeoIpDatabaseUpdaterService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GeoIpDatabaseUpdaterService> _logger;
        private readonly GeoIpSettings _settings;
        private CronExpression? _cronExpression;
        private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(15);

        public GeoIpDatabaseUpdaterService(
            IServiceProvider serviceProvider,
            IOptions<GeoIpSettings> settings,
            ILogger<GeoIpDatabaseUpdaterService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _settings = settings.Value;

            if (!string.IsNullOrWhiteSpace(_settings.UpdateScheduleCron))
            {
                try
                {
                    _cronExpression = CronExpression.Parse(_settings.UpdateScheduleCron, CronFormat.Standard);
                }
                catch (CronFormatException ex)
                {
                    _logger.LogError(ex, "GeoIP更新的CRON表达式无效: {CronExpression}", _settings.UpdateScheduleCron);
                    _cronExpression = null;
                }
            }
            else
            {
                _logger.LogInformation("GeoIP数据库更新计划(CRON)未配置。自动计划更新已禁用。");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GeoIp数据库更新服务正在启动。");
            stoppingToken.Register(() => _logger.LogInformation("GeoIp数据库更新服务正在停止。"));

            await Task.Delay(_initialDelay, stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("正在执行初始GeoIP数据库检查/更新。");
                await DoWorkAsync(stoppingToken);
            }

            if (_cronExpression == null)
            {
                _logger.LogInformation("GeoIP数据库自动更新计划已禁用。仅执行了初始更新（如有）。");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextOccurrence = _cronExpression.GetNextOccurrence(now);

                if (nextOccurrence.HasValue)
                {
                    var delay = nextOccurrence.Value - now;
                    if (delay.TotalMilliseconds <= 0)
                    {
                        _logger.LogWarning("下一次计划的GeoIP更新时间已过或就在当前。调整为1分钟后运行。");
                        delay = TimeSpan.FromMinutes(1);
                    }

                    _logger.LogInformation("下一次GeoIP数据库更新计划于: {NextOccurrenceUtc} (UTC) (等待 {Delay})", nextOccurrence.Value, delay);

                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await DoWorkAsync(stoppingToken);
                    }
                }
                else
                {
                    _logger.LogWarning("无法从CRON表达式确定下一次GeoIP数据库更新时间。计划更新将停止。");
                    break;
                }
            }
            _logger.LogInformation("GeoIp数据库更新服务已完成其执行循环。");
        }

        private async Task DoWorkAsync(CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested) return;

            _logger.LogInformation("GeoIP数据库更新服务正在尝试更新。");
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var geoIpService = scope.ServiceProvider.GetRequiredService<GeoIpService>();
                    await geoIpService.UpdateDatabaseAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "在 DoWorkAsync 中更新GeoIP数据库时发生错误。");
            }
        }

        public override Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("GeoIP数据库更新服务正在停止。");
            return base.StopAsync(stoppingToken);
        }
    }
}