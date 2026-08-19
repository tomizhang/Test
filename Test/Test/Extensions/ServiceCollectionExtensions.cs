using Common.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Extensions
{
    /// <summary>
    /// 依赖注入扩展方法 (用于 WinForms、WPF、ASP.NET Core 或 Generic Host)
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 向依赖注入容器注册量化回测引擎核心服务
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="lifetime">服务生命周期 (默认 Transient)</param>
        /// <returns>服务集合</returns>
        public static IServiceCollection AddBacktestEngine(
            this IServiceCollection services,
            ServiceLifetime lifetime = ServiceLifetime.Transient)
        {
            switch (lifetime)
            {
                case ServiceLifetime.Singleton:
                    services.AddSingleton<IBacktestEngineService, BacktestEngineService>();
                    break;
                case ServiceLifetime.Scoped:
                    services.AddScoped<IBacktestEngineService, BacktestEngineService>();
                    break;
                case ServiceLifetime.Transient:
                default:
                    services.AddTransient<IBacktestEngineService, BacktestEngineService>();
                    break;
            }

            return services;
        }
    }
}
