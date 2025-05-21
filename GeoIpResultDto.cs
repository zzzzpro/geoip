
namespace GeoIpApi
{
    public class GeoIpResultDto
    {
        public string? IpAddress { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? CountryIsoCode { get; set; }
        public string? Continent { get; set; }
        public string? PostalCode { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? TimeZone { get; set; }
        public string? ISP { get; set; }
        public string? Organization { get; set; }
        public long? AutonomousSystemNumber { get; set; } 
        public string? AutonomousSystemOrganization { get; set; }
        public string? Domain { get; set; }
        public bool? IsAnonymousProxy { get; set; }
        public bool? IsSatelliteProvider { get; set; }
    }
}