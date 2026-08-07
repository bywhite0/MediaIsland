
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

/// <summary>
/// 判断一份歌词是否应当「直接套用」——即跳过本机搜索流程，原样呈现。
/// </summary>
/// <remarks>
/// 提取成独立谓词是为了让这条路由规则可被测试。它内联在组件事件处理器里时，
/// 唯一能写的测试是同义反复（断言 <c>source == External</c> 等于「它是不是 External」），
/// 那种测试对规则的任何改动都不会失败。
/// </remarks>
internal static class LyricsInjectionRouting
{
    /// <param name="isExternalMediaEffective">
    /// 当前生效的**媒体**来自 MediaLink 注入。此时歌词无论来自哪里都应直接套用：
    /// 本机没有这首曲目的播放上下文，搜索也无从谈起。
    /// </param>
    /// <param name="lyricsSource">
    /// 歌词自身的来源。仅 <see cref="LyricsSourceId.External"/> 代表「由 MediaLink 注入」；
    /// <see cref="LyricsSourceId.LocalFile"/> 是用户导入并固定的本地文件，
    /// 它经由正常查找路径产生，必须走常规呈现流程而非注入分支——
    /// 两者混同会让本地固定歌词绕过搜索版本号与时钟同步。
    /// </param>
    public static bool ShouldApplyDirectly(bool isExternalMediaEffective, LyricsSourceId? lyricsSource) =>
        isExternalMediaEffective || lyricsSource == LyricsSourceId.External;
}
