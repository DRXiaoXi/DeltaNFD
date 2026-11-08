namespace DeltaNFD.Services;

/// <summary>本次公测的功能范围；旧偏好不能授权隐藏实验功能，恢复入口继续保留。</summary>
public static class ReleaseFeaturePolicy
{
    public static bool PendulumEnabled => false;
    public static bool FrameTimerIgnoreEnabled => false;
}
