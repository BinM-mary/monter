namespace WpfApp1.Models;

/// <summary>
/// 多帧请求的发送阶段时间参数（实施计划 §4.10）。SF 路径不读取本类型任何字段。
/// 具体数值仍需中间件与 Bootloader 契约确认，这里的默认值只用于可运行的基线，
/// 不代表已通过实机验证的协议参数。
/// </summary>
public sealed record DiagnosticSendTiming
{
    public static DiagnosticSendTiming Default { get; } = new();

    /// <summary>每片写入等待上限，防止等待串口任务无期限挂起。</summary>
    public TimeSpan WriteBudget { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>每片回显等待上限。</summary>
    public TimeSpan EchoBudget { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>相邻分片最小发送间隔；零表示写完立即发下一片。</summary>
    public TimeSpan MinimumInterFrameGap { get; init; } = TimeSpan.Zero;

    /// <summary>最大允许相邻间隔；超限不得快速补发，按发送失败处理。</summary>
    public TimeSpan InterFrameGapBudget { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>发送预算中的有界调度余量。</summary>
    public TimeSpan SchedulingMargin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>可选的显式总发送上限；提供时必须通过最坏预算可行性校验。</summary>
    public TimeSpan? SendBudgetOverride { get; init; }

    /// <summary>停止新 3C 后的固定回显隔离长度。</summary>
    public TimeSpan EchoDrainDuration { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>历史回显容忍窗口（自 t_last_complete 起），不得小于隔离长度。</summary>
    public TimeSpan EchoToleranceAfterLastComplete { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// 多帧请求的响应阶段时间参数（实施计划 §4.10）。
/// <see cref="FirstResponsePollDelay"/> 与 <see cref="ResponsePollPeriod"/> 分别生效：
/// 前者决定首次 3D，后者决定轮询节拍。
/// </summary>
public sealed record DiagnosticResponseTiming
{
    public static DiagnosticResponseTiming Default { get; } = new();

    /// <summary>自 t_last_complete 起算的响应总预算，必须覆盖固定隔离窗口。</summary>
    public TimeSpan ResponseBudget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>自 t_last_complete 起的首次 3D 延迟。</summary>
    public TimeSpan FirstResponsePollDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>3D 轮询节拍。</summary>
    public TimeSpan ResponsePollPeriod { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>响应 TP 的 N_Cr。</summary>
    public TimeSpan ConsecutiveFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);
}

/// <summary>
/// 多帧请求选项（实施计划 §4.1、§4.10）。长度 &gt; 6 的请求必须显式提供；
/// 入队前生成不可变快照并完成全部校验，运行期不再回读调用方对象。
/// </summary>
public sealed record DiagnosticMultiFrameOptions
{
    /// <summary>编码上限 4095（12 位长度）；设备可接收上限可能更小。</summary>
    public const int EncodingLengthLimit = 4095;

    public int MaxRequestLength { get; init; } = EncodingLengthLimit;

    public DiagnosticSendTiming Send { get; init; } = DiagnosticSendTiming.Default;

    public DiagnosticResponseTiming Response { get; init; } = DiagnosticResponseTiming.Default;
}
