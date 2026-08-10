using MediaIsland.Models;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// MediaLink 全部服务的注册。
///
/// 从插件入口里抽出来，是为了让这张对象图能被测试构建一次。图里有两处即时解析
/// （上游服务的工厂里解析服务端），它们的正确性依赖一条外部约束——服务端的工厂
/// 不得反过来解析上游服务。这条约束一旦被打破，失效形态是插件加载期的构造期环，
/// 表现为硬崩而非报错，而插件入口本身依赖宿主框架，测试驱动不了。
///
/// 本方法不注册任何依赖宿主框架的东西（组件、设置页），那些留在插件入口里。
/// </summary>
public static class MediaLinkServiceCollectionExtensions
{
    /// <summary>
    /// <paramref name="settingsAccessor"/> 而非直接取插件单例：
    /// <see cref="MediaSourceCoordinator"/> 的构造函数就会求值它，
    /// 图里写死插件单例就等于要求「必须有个已初始化的插件」，那正是测试建不起图的原因。
    ///
    /// 前置：调用方须已注册 <see cref="IMediaService"/>、<see cref="LyricsSearchService"/>
    /// 与 <see cref="MediaPlatformProviderResolver"/>。
    /// </summary>
    public static IServiceCollection AddMediaLink(
        this IServiceCollection services,
        Func<PluginSettings> settingsAccessor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settingsAccessor);

        services.AddSingleton<MediaLinkInjectionStore>();

        // 分析层三件套：需求计数、共享分析器、门面。全是单例——FFT 贵且与观察者无关，
        // N 个频谱组件共用一次计算。
        services.AddSingleton<AudioVisualizationDemand>();
        services.AddSingleton<AudioSpectrumAnalyzer>();
        services.AddSingleton<AudioVisualizationService>();

        services.AddSingleton<MediaSourceCoordinator>(provider => new MediaSourceCoordinator(
            provider.GetRequiredService<IMediaService>(),
            provider.GetRequiredService<LyricsSearchService>(),
            provider.GetRequiredService<MediaLinkInjectionStore>(),
            settingsAccessor));
        services.AddSingleton<IEffectiveMediaSource>(provider =>
            provider.GetRequiredService<MediaSourceCoordinator>());

        services.AddSingleton<MediaLinkHostedService>(provider => new MediaLinkHostedService(
            provider.GetRequiredService<IMediaService>(),
            provider.GetRequiredService<LyricsSearchService>(),
            provider.GetRequiredService<MediaLinkInjectionStore>(),
            provider.GetRequiredService<MediaSourceCoordinator>(),
            provider.GetRequiredService<MediaPlatformProviderResolver>(),
            settingsAccessor,
            provider.GetService<ILoggerFactory>(),
            provider.GetRequiredService<AudioVisualizationDemand>(),
            provider.GetRequiredService<AudioVisualizationService>(),
            // 仲裁锚点是生效媒体来源，不是连接态：连着上游不等于该用上游的声音。
            // 用 lambda 延迟解析，与下面上游服务的接线同理。
            () => provider.GetRequiredService<MediaSourceCoordinator>().IsExternalMediaEffective));
        services.AddSingleton<IMediaLinkGateway>(provider =>
            provider.GetRequiredService<MediaLinkHostedService>());
        services.AddHostedService(provider => provider.GetRequiredService<MediaLinkHostedService>());

        // 上游消费与服务端相互独立：一台实例可以只推、只收，或两者同时（转发中继）。
        services.AddSingleton<MediaLinkUpstreamHostedService>(provider =>
        {
            var upstream = new MediaLinkUpstreamHostedService(
                provider.GetRequiredService<MediaLinkInjectionStore>(),
                provider.GetRequiredService<MediaSourceCoordinator>(),
                settingsAccessor,
                provider.GetService<ILoggerFactory>(),
                visualization: provider.GetRequiredService<AudioVisualizationService>(),
                visualizationDemand: provider.GetRequiredService<AudioVisualizationDemand>(),
                // 用 lambda 延迟解析：两个宿主服务互相引用，直接注入会形成构造期循环依赖。
                downstreamAudioDemandAccessor: () =>
                    provider.GetRequiredService<MediaLinkHostedService>().HasDownstreamAudioDemand,
                // 转发目标是本机服务端的广播入口。同样用委托而不是互相注入。
                audioForwarder: (frame, ct) =>
                    provider.GetRequiredService<MediaLinkHostedService>()
                        .BroadcastAudioFrameAsync(frame, ct));

            // 三条重算边接在两个宿主服务之外。挂事件必须持有发布方实例，故这里只能即时解析，
            // 委托形式做不到。安全性由依赖方向单向保证：服务端的工厂不解析上游服务，
            // 故不存在构造期环。加了反向解析就会环，这一点由注册测试守着。
            MediaLinkAudioWiring.Connect(
                upstream,
                provider.GetRequiredService<MediaLinkHostedService>(),
                provider.GetRequiredService<MediaSourceCoordinator>());
            return upstream;
        });
        services.AddHostedService(provider => provider.GetRequiredService<MediaLinkUpstreamHostedService>());

        return services;
    }
}
