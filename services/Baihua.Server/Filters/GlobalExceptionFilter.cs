using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Net;
using Baihua.Core.Localization;

namespace Baihua.Server.Filters
{
    public class GlobalExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<GlobalExceptionFilter> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger, IStringLocalizer<SharedResources> loc)
        {
            _logger = logger;
            _loc = loc;
        }

        public void OnException(ExceptionContext context)
        {
            var exception = context.Exception;
            var requestPath = context.HttpContext.Request.Path;
            var requestMethod = context.HttpContext.Request.Method;

            // 记录结构化日志（展开内部异常，便于诊断 DbUpdateException → PostgresException）
            var inner = exception.InnerException;
            _logger.LogError(
                exception,
                "Unhandled exception occurred at {Method} {Path}: {ExceptionMessage} | Inner: {InnerType}: {InnerMessage}",
                requestMethod,
                requestPath,
                exception.Message,
                inner?.GetType().Name ?? "none",
                inner?.Message ?? ""
            );

            // 返回统一的错误响应（不泄露内部异常信息）
            context.Result = new ObjectResult(new
            {
                Success = false,
                Message = _loc["Error_InternalServerError"],
                RequestId = context.HttpContext.TraceIdentifier
            })
            {
                StatusCode = (int)HttpStatusCode.InternalServerError
            };

            context.ExceptionHandled = true;
        }
    }
}