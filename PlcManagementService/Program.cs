using System;
using System.ServiceProcess;

namespace PLCService
{
    static class Program
    {
        static void Main()
        {
            // 调试模式：直接运行服务逻辑
            if (Environment.UserInteractive)
            {
                var service = new PLCService();
                Console.WriteLine("PLC服务调试模式启动...");
                Console.WriteLine("按任意键停止服务...");

                service.DebugStart();
                Console.Read();
                service.DebugStop();
            }
            else
            {
                // 生产环境：作为Windows服务运行
                ServiceBase[] ServicesToRun;
                ServicesToRun = new ServiceBase[]
                {
                    new PLCService()
                };
                ServiceBase.Run(ServicesToRun);
            }
        }
    }
}