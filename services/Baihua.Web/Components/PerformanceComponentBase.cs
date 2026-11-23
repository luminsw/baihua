using Microsoft.AspNetCore.Components;
using Baihua.Web.Services;

namespace Baihua.Web.Components;


/// <summary>
/// 性能监控组件基类 - 自动记录组件渲染时间
/// 用法：继承此类，或使用 @inherits PerformanceComponentBase
/// 
/// 支持端到端性能追踪：
/// - 在 OnInitialized 中调用 SetCurrentTraceId(traceId) 关联 E2E 追踪
/// - 组件渲染完成会自动记录到 E2EPerformanceService
/// </summary>
public class PerformanceComponentBase : ComponentBase, IDisposable
{
    [Inject]
    protected ComponentPerformanceService? PerformanceService { get; set; }
    
    [Inject]
    protected EndToEndPerformanceService? E2EPerformanceService { get; set; }
    
    private RenderToken? _renderToken;
    private string? _componentName;
    private string? _currentTraceId;
    
    /// <summary>
    /// 是否启用性能监控（可在子类中覆盖禁用）
    /// </summary>
    protected virtual bool EnablePerformanceTracking => true;
    
    /// <summary>
    /// 自定义组件名称（默认使用类名）
    /// </summary>
    protected virtual string? CustomComponentName => null;
    
    /// <summary>
    /// 设置当前 E2E 追踪 ID，将组件渲染关联到端到端追踪
    /// </summary>
    protected void SetCurrentTraceId(string traceId)
    {
        _currentTraceId = traceId;
    }
    
    protected override void OnInitialized()
    {
        _componentName = CustomComponentName ?? GetType().Name;
        
        if (EnablePerformanceTracking && PerformanceService != null)
        {
            _renderToken = PerformanceService.BeginRender(_componentName);
        }
        
        // 自动创建 E2E trace（如果没有设置 trace id）
        if (E2EPerformanceService != null && _currentTraceId == null)
        {
            _currentTraceId = E2EPerformanceService.StartTrace(_componentName, _componentName);
        }
        
        base.OnInitialized();
    }
    
    protected override void OnParametersSet()
    {
        // 如果 OnInitialized 没有创建 token（比如在预渲染中），在这里创建
        if (EnablePerformanceTracking && PerformanceService != null && _renderToken == null)
        {
            _componentName ??= GetType().Name;
            _renderToken = PerformanceService.BeginRender(_componentName);
        }
        
        base.OnParametersSet();
    }
    
    protected override void OnAfterRender(bool firstRender)
    {
        if (EnablePerformanceTracking && PerformanceService != null && _renderToken != null)
        {
            // 先获取渲染时间（EndRender 内部会计算）
            var elapsedMs = _renderToken.Stopwatch.ElapsedMilliseconds;
            
            PerformanceService.EndRender(_renderToken, firstRender);
            
            // 记录到端到端追踪
            if (_currentTraceId != null && E2EPerformanceService != null && elapsedMs > 0)
            {
                E2EPerformanceService.RecordComponentRender(_currentTraceId, _componentName!, elapsedMs);
            }
            
            _renderToken = null; // 清空，下次渲染会重新创建
        }
        
        base.OnAfterRender(firstRender);
    }

    public void Dispose()
    {
        if (_currentTraceId != null && E2EPerformanceService != null)
        {
            E2EPerformanceService.EndTrace(_currentTraceId);
            _currentTraceId = null;
        }
    }
}
