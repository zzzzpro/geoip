using GeoIpApi;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http; 
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging; 

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    
});

builder.Services.Configure<GeoIpSettings>(
    builder.Configuration.GetSection(GeoIpSettings.SectionName));

builder.Services.AddHttpClient<GeoIpService>(); // 注册类型化的 HttpClient
builder.Services.AddSingleton<GeoIpService>();
builder.Services.AddHostedService<GeoIpDatabaseUpdaterService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "GeoIP API", Version = "v1" });
});

var app = builder.Build();

// 3. 确保数据目录存在 (GeoIpService 内部也有检查)
var geoIpSettings = app.Services.GetRequiredService<IOptions<GeoIpSettings>>().Value;
if (!string.IsNullOrWhiteSpace(geoIpSettings.DatabasePath))
{
    var dbDir = Path.GetDirectoryName(geoIpSettings.DatabasePath);
    if (!string.IsNullOrWhiteSpace(dbDir) && !Directory.Exists(dbDir))
    {
        app.Logger.LogInformation("启动时创建数据目录: {Directory}", dbDir);
        Directory.CreateDirectory(dbDir);
    }
}

// 4. 配置 HTTP 请求管道
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "GeoIP API V1"));
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();

// 配置 Forwarded Headers 中间件，以便在反向代理后正确获取客户端IP
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// 5. 定义 API 端点
app.MapGet("/geoip/{ipAddress?}", async (string? ipAddress, HttpContext context, GeoIpService geoIpService, ILogger<Program> logger) =>
{
    string? ipToLookup = ipAddress;

    if (string.IsNullOrWhiteSpace(ipToLookup))
    {
        ipToLookup = context.Connection.RemoteIpAddress?.ToString();
        logger.LogInformation("路径中未提供IP，使用客户端IP: {RemoteIp}", ipToLookup);
    }
    else
    {
        if (!IPAddress.TryParse(ipAddress, out _))
        {
            logger.LogWarning("路径中提供的IP地址格式无效: {IpAddress}", ipAddress);
            // 返回标准的 ValidationProblemDetails
            var errors = new Dictionary<string, string[]> { { "ipAddress", new[] { "路径中提供的IP地址格式无效。" } } };
            return Results.ValidationProblem(errors, title: "输入验证失败", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    if (string.IsNullOrWhiteSpace(ipToLookup))
    {
        logger.LogWarning("无法确定要查询的客户端IP地址。");
        // 返回标准的 ProblemDetails
        return Results.Problem(
            detail: "无法确定IP地址。请在路径中提供，或确保代理头已正确配置和转发。",
            statusCode: StatusCodes.Status400BadRequest,
            title: "无法确定IP地址"
        );
    }

    if (IPAddress.TryParse(ipToLookup, out var parsedIp) && IPAddress.IsLoopback(parsedIp))
    {
        logger.LogInformation("IP地址是环回地址 ({LoopbackIp})，GeoIP查询可能无意义或数据库中不可用。", ipToLookup);
    }

    logger.LogInformation("正在查询IP {ClientIp} 的GeoIP数据。", ipToLookup);
    var geoData = geoIpService.GetGeoData(ipToLookup);

    if (geoData == null)
    {
        return Results.Problem(
            detail: $"未找到IP地址 {ipToLookup} 的GeoIP数据，或者IP无效/是私有地址/是数据库中不存在的环回地址。",
            statusCode: StatusCodes.Status404NotFound,
            title: "未找到GeoIP数据"
        );
    }

    var result = new GeoIpResultDto
    {
        IpAddress = ipToLookup ?? "", 
        City = geoData.City?.Name ?? "",
        Country = geoData.Country?.Name ?? "",
        CountryIsoCode = geoData.Country?.IsoCode ?? "",
        Continent = geoData.Continent?.Name ?? "",
        PostalCode = geoData.Postal?.Code ?? "",
        Latitude = geoData.Location?.Latitude,
        Longitude = geoData.Location?.Longitude, 
        TimeZone = geoData.Location?.TimeZone ?? "",
        ISP = geoData.Traits?.Isp ?? "",
        Organization = geoData.Traits?.Organization ?? "",
        AutonomousSystemNumber = geoData.Traits?.AutonomousSystemNumber,
        AutonomousSystemOrganization = geoData.Traits?.AutonomousSystemOrganization ?? "",
        Domain = geoData.Traits?.Domain ?? "",
        IsAnonymousProxy = geoData.Traits?.IsAnonymousProxy,
        IsSatelliteProvider = geoData.Traits?.IsSatelliteProvider, 
    };

    return Results.Ok(result);
})
.WithName("GetGeoIpData")
.WithOpenApi(); // 确保为 Swagger/OpenAPI 生成元数据

app.MapGet("/", (HttpContext context, ILogger<Program> logger) => {
    logger.LogInformation("访问根路径 /");
    return "GeoIP API 正在运行。请尝试 /api/geoip/{ip_地址} 或 /api/geoip/";
});

app.Run();

[JsonSerializable(typeof(GeoIpResultDto))] 
[JsonSerializable(typeof(ProblemDetails))] 
[JsonSerializable(typeof(HttpValidationProblemDetails))] 

internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
