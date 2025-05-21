namespace GeoIpApi
{
    public class GeoIpSettings
    {
        public const string SectionName = "GeoIp";

        public string? DatabasePath { get; set; }
        public string? DatabaseDownloadUrl { get; set; }
        public string? UpdateScheduleCron { get; set; }
    }
}