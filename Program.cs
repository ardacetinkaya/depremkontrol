namespace Earthquake.Checker
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
            .ConfigureHostConfiguration(config =>
            {

                if (args != null)
                {
                    config.AddCommandLine(args);
                }
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.Configure<WorkerSettings>(hostContext.Configuration.GetSection("WorkerSettings"));

                services.AddHostedService<Worker>();

            }).ConfigureLogging((hostContext, configLogging) =>
            {
                configLogging.AddConsole();
                configLogging.AddDebug();
            });
    }
}
