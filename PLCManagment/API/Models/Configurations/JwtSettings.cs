namespace PLCManagement.API.Models.Configurations
{
    public class JwtSettings
    {
        public required string Secret { get; set; }
        public required string Issuer { get; set; }
        public required string Audience { get; set; }
        public int ExpireMinutes { get; set; }
        public int RefreshExpireDays { get; set; }
    }
}