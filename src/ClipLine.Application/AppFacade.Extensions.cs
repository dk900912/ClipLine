namespace ClipLine.Application;

/// <summary>
/// 门面扩展点：P2–P5 在此追加新的输入分支。
/// 刻意用 partial 方法而不是新接口 —— 保证整个 v1 只有一个测试接缝。
/// </summary>
public sealed partial class AppFacade
{
    partial void DispatchExtension(AppInput input)
    {
        switch (input)
        {
            // P2 起在此追加 case；P1 阶段为空实现。
            default: break;
        }
    }
}
