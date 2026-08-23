using MediaIsland.Services.MediaLink.Protocol;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 服务端一条 server.hello 里，判定跨机对齐要用到的那部分声明：可选能力集合，
/// 以及 audio.clock 给出的播放延迟预算。
///
/// 为什么装成一个不可变快照而不是两个字段 —— 判定「能不能对齐、不能是因为什么」要同时
/// 吃这两个值，而它们由握手线程写、由播放侧读。两个字段各写一次就没有配对手段：
/// server.hello 在连接存活期间可以重发，重发那一瞬读者会拿到新能力配旧预算，或者反过来。
/// 后果不是某个数值抖一下，是归因指错方向 —— 新 hello 已经撤掉 audio.clock，读者却报
/// 「服务端没声明预算」，那条提示让人去查服务端配置，实际该查的是服务端版本。
///
/// 引用赋值本身是原子的，故换掉整份声明只需一次 Volatile.Write，而读者一次 Volatile.Read
/// 取到的两个值必然出自同一条 hello。
/// </summary>
/// <param name="Capabilities">这条 hello 声明的可选能力；缺该字段的老服务端落到空集合。</param>
/// <param name="AudioClockBudgetMs">
/// audio.clock 声明的播放延迟预算，毫秒；null 表示这条 hello 没有声明。
///
/// 存成 long? 而不是拿某个 long 值当空值哨兵：哨兵总会与合法声明值相撞 —— 线上的 dMs
/// 本身就是可空整数，服务端发得出 long.MinValue，那条声明于是被读成「没声明」。而
/// 「声明了一个办不到的值」与「没声明」的排查方向不同（改服务端的配置值 / 查服务端版本），
/// 撞上就是把前者吞成后者。至于可空类型没有原子读写这一层顾虑，随快照一并消失：
/// 原子性由那一次引用写承担，字段本身不再被并发读写。
/// </param>
internal sealed record MediaLinkServerDeclaration(
    IReadOnlyCollection<string> Capabilities,
    long? AudioClockBudgetMs)
{
    /// <summary>还没收到过 hello 时的初值：不声明任何能力，也没有预算。</summary>
    public static readonly MediaLinkServerDeclaration None = new([], null);

    /// <summary>
    /// 对端的 capabilities 里有没有 audio.clock。它与预算是成对解读的两半，故放在这里
    /// 而不是让读者自己再去取一次能力集合 —— 分两次取回的两半可能来自两条 hello。
    /// </summary>
    public bool SupportsAudioClock => Capabilities.Contains(MediaLinkProtocol.CapabilityAudioClock);
}
