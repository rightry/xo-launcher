using System;
using System.Threading;
using System.Threading.Tasks;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 拉取内置 AI 模型的服务商图标。走 <see cref="RemoteIconLoader"/>（先校验地址与字节再解码），
    /// 取到后写回 <see cref="AiModelOption.Icon"/>，由下拉项的绑定换成真图标；
    /// 取不到就保持为空，下拉里继续显示占位字形。
    ///
    /// 只在第一次调用时跑一轮，失败不重试：这是装饰性资源，不值得为它做退避与超时管理，
    /// 下次启动会再试。
    /// </summary>
    internal static class AiModelIconService
    {
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _ = RunAsync();
        }

        private static async Task RunAsync()
        {
            foreach (var option in AiModels.All)
            {
                if (string.IsNullOrWhiteSpace(option.IconUrl) || option.HasIcon) continue;
                try
                {
                    var bytes = await RemoteIconLoader.DownloadAsync(option.IconUrl, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (bytes is null) continue;
                    var image = await RemoteIconLoader.CreateBitmapAsync(bytes, 40, 40).ConfigureAwait(false);
                    if (image is null) continue;
                    UiDispatch.Run(() => option.SetIcon(image));
                }
                catch (Exception)
                {
                    // 图标取不到不影响任何功能，留着占位字形即可
                }
            }
        }
    }
}
