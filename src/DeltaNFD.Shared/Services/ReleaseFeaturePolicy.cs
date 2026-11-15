namespace DeltaNFD.Services;

/// <summary>当前功能范围；钟摆计划沿用各系统准入，隐藏功能的旧偏好不能授权执行。</summary>
public static class ReleaseFeaturePolicy
{
    public static bool PendulumEnabled => false;
    public static bool FrameTimerIgnoreEnabled => false;
    public static bool DriverPriorityEnabled => false;
}
