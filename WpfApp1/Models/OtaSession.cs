namespace WpfApp1.Models;

/// <summary>
/// OTA 升级流程的阶段（架构/OTA升级流程.md）。枚举值与文档步骤编号一一对应：
/// 第一步在线确认、第二步软件版本、第三步 Bootloader 版本、第四步硬件版本、
/// 第五步再次在线确认、第六步进入扩展诊断会话、第七步进入编程会话、
/// 第八步请求 Seed、第九步发送 Key、第十步擦除、
/// 第十一步申请下载、第十二步逐块传输、第十三步结束传输、第十四步固件校验、
/// 第十五步设备复位。
/// <para>
/// 第三、第四步当前默认不执行（`OtaUpgradeService.ReadBootloaderAndHardwareVersions = false`，
/// 只读 APP 版本），编号仍保留以便随时恢复，因此枚举中这两项在默认配置下不会出现在分步报告里。
/// </para>
/// </summary>
public enum OtaStage
{
    /// <summary>尚未进入流程。</summary>
    NotStarted = 0,

    /// <summary>第一步：暂停应用通信后的 TesterPresent 连续在线确认。</summary>
    OnlineProbe = 1,

    /// <summary>第二步：读取软件版本（固件版本，DID F1 95）。</summary>
    SoftwareVersion = 2,

    /// <summary>第三步：读取 Bootloader 版本（DID F1 80）。</summary>
    BootloaderVersion = 3,

    /// <summary>第四步：读取硬件版本（DID F1 93）。</summary>
    HardwareVersion = 4,

    /// <summary>第五步：再次进行两次在线确认（目标连续成功 2 次，计数独立初始化）。</summary>
    OnlineProbeAgain = 5,

    /// <summary>第六步：发送 10 03 进入扩展诊断会话。</summary>
    ExtendedSession = 6,

    /// <summary>第七步：发送 10 02 进入编程会话（只进入会话，不含安全访问与下载）。</summary>
    ProgrammingSession = 7,

    /// <summary>第八步：发送 27 09 请求 Seed（正响应 67 09 + 四字节 Seed）。</summary>
    SeedRequest = 8,

    /// <summary>第九步：按中间件算法计算 Key 并发送 27 0A（正响应恰好 67 0A）。</summary>
    KeySend = 9,

    /// <summary>
    /// 第十步：擦除（31 01 FF 00 + 起始地址 + 字节长度，12 字节多帧请求）。
    /// 正响应 71 01 FF 00 只表示设备接受例程，不等于擦除完成（§10.6）。
    /// </summary>
    Erase = 10,

    /// <summary>
    /// 第十一步：申请下载（`34 00 44 + 起始地址 + 字节长度`，11 字节多帧请求），
    /// 正响应 `74` 携带设备最大块长（§11.1、§11.2）。
    /// </summary>
    DownloadRequest = 11,

    /// <summary>
    /// 第十二步：逐块传输固件（`36 + 块号 + 数据`，每块等待 `76 块号` 确认，§12.1、§12.2）。
    /// 只有收到匹配块号的 76 才推进偏移与块号。
    /// </summary>
    DataTransfer = 12,

    /// <summary>第十三步：结束数据传输（单字节 `37`，正响应恰好 `77`，§13.1、§13.3）。</summary>
    TransferExit = 13,

    /// <summary>
    /// 第十四步：固件校验（`31 01 02 02 + 地址 + 大小 + CRC32`，16 字节多帧请求），
    /// 结果 00 通过、01 失败、其他值按未知结果失败（§14.4）。
    /// </summary>
    Verification = 14,

    /// <summary>
    /// 第十五步：设备复位（两字节 `11 01`，正响应恰好 `51 01`，§15.1、§15.3）。
    /// 只在校验返回 00 之后发送；复位肯定响应不等于新固件已启动运行（§15.5）。
    /// </summary>
    EcuReset = 15,

    /// <summary>第一至第十五步全部完成；设备启动运行需另行实机验证（§15.5）。</summary>
    Completed = 16
}

/// <summary>
/// 第十步擦除的最终结果快照（§10.5、§10.6）：
/// 提交的实际范围、设备正响应的状态记录原样，以及两个判定标志。
/// <para>
/// <see cref="RequestAccepted"/> 只表示收到 71 01 FF 00（例程被接受），
/// <see cref="CompletionConfirmed"/> 才表示确认擦除完成；设备完成响应契约未确认前后者恒为 false，
/// 不把“示例程已接受”当成“已擦除”（§10.6）。
/// </para>
/// </summary>
public sealed record OtaEraseOutcome(
    uint StartAddress,
    uint Length,
    byte[] StateRecord,
    bool RequestAccepted,
    bool CompletionConfirmed)
{
    /// <summary>状态记录的十六进制文本；无状态记录时为空字符串。</summary>
    public string StateRecordText => StateRecord.Length == 0
        ? string.Empty
        : BitConverter.ToString(StateRecord).Replace('-', ' ');
}

/// <summary>
/// 第十一至第十三步的下载结果快照（§11.2、§12.3、§13.3）：
/// 设备返回的最大块长、本阶段实际每包固件字节数、总块数与已确认字节数，以及是否已收到 77。
/// <see cref="ConfirmedBytes"/> 只统计收到匹配块号 76 的固件字节，不含 TP 填充。
/// </summary>
public sealed record OtaDownloadOutcome(
    uint MaxNumberOfBlockLength,
    int PayloadCapacity,
    int BlockCount,
    int ConfirmedBytes,
    int TotalBytes,
    bool TransferExited)
{
    /// <summary>已确认字节数与固件总字节数的文本，例如 24,476 / 24,476 字节。</summary>
    public string ConfirmedBytesText =>
        $"{ConfirmedBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} / " +
        $"{TotalBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 字节";
}

/// <summary>
/// 第十四步固件校验的结果快照（§14.4、§14.6）：
/// 参与计算的 CRC32 数值、设备返回的结果字节与是否通过。
/// 与真实设备的一致性核对仍待实现侧确认，<see cref="Succeeded"/> 只表示设备回了 00。
/// </summary>
public sealed record OtaVerificationOutcome(uint Crc32, byte Result, bool Succeeded)
{
    /// <summary>CRC32 数值文本，例如 0xB0051F35。</summary>
    public string Crc32Text => "0x" + Crc32.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>结果字节文本；00 校验通过、01 校验失败、其他为未知结果。</summary>
    public string ResultText => Result switch
    {
        0x00 => "00（校验通过）",
        0x01 => "01（校验失败）",
        _ => $"0x{Result:X2}（未知结果，按失败处理）"
    };
}

/// <summary>
/// 第十五步设备复位的结果快照（§15.3、§15.5、§15.7）：
/// 记录设备返回的两字节正响应 `51 01`，以及复位确认后等待 50 ms 读到的一次 APP 版本。
/// 该结果只证明设备接受了复位请求，不代表新固件已经启动并正常运行；
/// <see cref="AppVersionAfterReset"/> 为 `null` 表示复位后那次版本读取失败（设备可能仍在启动），
/// 不影响本次升级结论。
/// </summary>
public sealed record OtaResetOutcome(byte[] Response, string? AppVersionAfterReset = null)
{
    /// <summary>正响应的十六进制文本，例如 51 01。</summary>
    public string ResponseText => Response.Length == 0
        ? string.Empty
        : BitConverter.ToString(Response).Replace('-', ' ');

    /// <summary>复位后读到的 APP 版本；未读到为 null。</summary>
    public string AppVersionAfterResetText => AppVersionAfterReset ?? "未读到";
}

/// <summary>
/// 一次 OTA 过程中的分步进展（§1.6 日志要求）：阶段、本阶段序号、单次结果与两个计数。
/// 由 OtaUpgradeService 在后台线程上报，界面负责切回 UI 线程后记录。
/// <para>
/// 第十二步按块上报时用 <see cref="CompletedBytes"/> / <see cref="TotalBytes"/> 携带已确认字节与总字节，
/// 供界面计算传输进度；其余阶段两者为 0，界面按阶段映射固定百分比。
/// </para>
/// </summary>
public sealed record OtaStepReport(
    OtaStage Stage,
    int AttemptNumber,
    bool Success,
    string Message,
    int ConsecutiveSuccessCount,
    int CumulativeFailureCount)
{
    /// <summary>本阶段已确认的固件字节数（目前仅第十二步使用）。</summary>
    public int CompletedBytes { get; init; }

    /// <summary>本次传输的固件总字节数（目前仅第十二步使用）。</summary>
    public int TotalBytes { get; init; }
}

/// <summary>
/// 一次 OTA（当前为第一至第十五步）的结果：是否成功、进展到的阶段、失败阶段、
/// 读取到的版本、擦除结果、下载结果、校验结果与复位结果。
/// </summary>
public sealed class OtaSessionResult
{
    private OtaSessionResult(
        bool success,
        OtaStage reachedStage,
        OtaStage? failedStage,
        string? failureText,
        int consecutiveProbeSuccessCount,
        int cumulativeProbeFailureCount,
        string? softwareVersion,
        string? bootloaderVersion,
        string? hardwareVersion,
        bool extendedSessionEntered,
        OtaEraseOutcome? eraseOutcome,
        OtaDownloadOutcome? downloadOutcome,
        OtaVerificationOutcome? verificationOutcome,
        OtaResetOutcome? resetOutcome)
    {
        Success = success;
        ReachedStage = reachedStage;
        FailedStage = failedStage;
        FailureText = failureText;
        ConsecutiveProbeSuccessCount = consecutiveProbeSuccessCount;
        CumulativeProbeFailureCount = cumulativeProbeFailureCount;
        SoftwareVersion = softwareVersion;
        BootloaderVersion = bootloaderVersion;
        HardwareVersion = hardwareVersion;
        ExtendedSessionEntered = extendedSessionEntered;
        EraseOutcome = eraseOutcome;
        DownloadOutcome = downloadOutcome;
        VerificationOutcome = verificationOutcome;
        ResetOutcome = resetOutcome;
    }

    public bool Success { get; }

    /// <summary>本次 OTA 进展到的最后阶段（失败时为失败发生前的阶段）。</summary>
    public OtaStage ReachedStage { get; }

    /// <summary>失败阶段；成功时为 null。</summary>
    public OtaStage? FailedStage { get; }

    public string? FailureText { get; }

    /// <summary>在线确认阶段的最终连续成功计数（§1.5）。</summary>
    public int ConsecutiveProbeSuccessCount { get; }

    /// <summary>在线确认阶段的累计失败计数（§1.5）。</summary>
    public int CumulativeProbeFailureCount { get; }

    public string? SoftwareVersion { get; }

    public string? BootloaderVersion { get; }

    public string? HardwareVersion { get; }

    /// <summary>
    /// 第六步是否已成功进入扩展诊断会话（成功后不发送 10 01 退出，按 §6.7 待后续确认）。
    /// 第七步失败时仍为 true：失败只记在 <see cref="FailedStage"/>，不回填、不清除前序步骤的成果（§7.4）。
    /// </summary>
    public bool ExtendedSessionEntered { get; }

    /// <summary>
    /// 第十步擦除的结果快照；第八步及之前失败、或擦除参数未生成时为 null。
    /// 注意 <see cref="OtaEraseOutcome.RequestAccepted"/> 不等于擦除完成（§10.6）。
    /// </summary>
    public OtaEraseOutcome? EraseOutcome { get; }

    /// <summary>
    /// 第十一至第十三步的下载结果；擦除及之前失败、或下载申请未成功时为 null。
    /// <see cref="OtaDownloadOutcome.TransferExited"/> 表示已收到 77，不代表校验通过。
    /// </summary>
    public OtaDownloadOutcome? DownloadOutcome { get; }

    /// <summary>
    /// 第十四步固件校验结果；校验请求未发出或传输层失败时为 null。
    /// <see cref="OtaVerificationOutcome.Succeeded"/> 只表示设备返回结果 00（§14.4）。
    /// </summary>
    public OtaVerificationOutcome? VerificationOutcome { get; }

    /// <summary>
    /// 第十五步设备复位结果；校验未通过、复位请求未发出或未收到 51 01 时为 null。
    /// 有值只表示设备返回了复位肯定响应，不代表新固件已启动运行（§15.5）。
    /// </summary>
    public OtaResetOutcome? ResetOutcome { get; }

    public static OtaSessionResult Succeeded(
        int consecutiveProbeSuccessCount,
        int cumulativeProbeFailureCount,
        string? softwareVersion,
        string? bootloaderVersion,
        string? hardwareVersion,
        OtaEraseOutcome? eraseOutcome = null,
        OtaDownloadOutcome? downloadOutcome = null,
        OtaVerificationOutcome? verificationOutcome = null,
        OtaResetOutcome? resetOutcome = null,
        bool extendedSessionEntered = true) =>
        new(true, OtaStage.Completed, null, null,
            consecutiveProbeSuccessCount, cumulativeProbeFailureCount,
            softwareVersion, bootloaderVersion, hardwareVersion, extendedSessionEntered,
            eraseOutcome, downloadOutcome, verificationOutcome, resetOutcome);

    public static OtaSessionResult Failed(
        OtaStage failedStage,
        string failureText,
        int consecutiveProbeSuccessCount,
        int cumulativeProbeFailureCount,
        string? softwareVersion = null,
        string? bootloaderVersion = null,
        string? hardwareVersion = null,
        OtaEraseOutcome? eraseOutcome = null,
        OtaDownloadOutcome? downloadOutcome = null,
        OtaVerificationOutcome? verificationOutcome = null,
        OtaResetOutcome? resetOutcome = null) =>
        new(false, failedStage, failedStage, failureText,
            consecutiveProbeSuccessCount, cumulativeProbeFailureCount,
            softwareVersion, bootloaderVersion, hardwareVersion,
            // 第七步及之后失败时上一笔 10 03 已成功，不能把扩展会话结果回填成“未进入”。
            failedStage > OtaStage.ExtendedSession,
            eraseOutcome, downloadOutcome, verificationOutcome, resetOutcome);
}
