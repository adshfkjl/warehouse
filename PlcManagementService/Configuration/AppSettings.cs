using System.Configuration;


namespace PLCService.Configuration
{
    public class AppSettings
    {
        public static string ConnectionString => ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;
        public static int HeartbeatInterval => int.Parse(ConfigurationManager.AppSettings["HeartbeatInterval"] ?? "1000");
        public static int ReadInterval => int.Parse(ConfigurationManager.AppSettings["ReadInterval"] ?? "1000");
        public static int FastReadInterval => int.Parse(ConfigurationManager.AppSettings["FastReadInterval"] ?? "200"); // 新增
        public static int ReconnectInterval => int.Parse(ConfigurationManager.AppSettings["ReconnectInterval"] ?? "60000");
        public static int MaxRetryCount => int.Parse(ConfigurationManager.AppSettings["MaxRetryCount"] ?? "5");
        public static int AutoRunUpdateInterval => int.Parse(ConfigurationManager.AppSettings["AutoRunUpdateInterval"] ?? "3000");
        public static int PlcConnectTimeout => int.Parse(ConfigurationManager.AppSettings["PlcConnectTimeout"] ?? "3000");
        public static int DbCommandTimeout => int.Parse(ConfigurationManager.AppSettings["DbCommandTimeout"] ?? "10");
    }
}
