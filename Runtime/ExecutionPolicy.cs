namespace TiaMcpServer;

/// <summary>
/// V4.3.0 统一执行策略状态机。执行模式是唯一事实来源；
/// 旧版 BeginnerMode / AutoApprove 仅作为兼容视图，不再独立控制执行行为。
/// </summary>
public enum ExecutionMode
{
    Safe = 0,
    Assist = 1,
    Auto = 2
}

public sealed class ExecutionPolicy
{
    public ExecutionMode Mode { get; private set; } = ExecutionMode.Safe;

    public void SetMode(ExecutionMode mode)
    {
        Mode = mode;
    }

    public bool BeginnerMode => Mode == ExecutionMode.Safe;
    public bool AutoApproveDangerous => Mode == ExecutionMode.Auto;
}
