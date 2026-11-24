using System.Text.Encodings.Web;

namespace Baihua.Web.Services;

/// <summary>
/// /open-webui 入口页：AI 对话由独立部署的 Open WebUI 提供。
///
/// 本仓库的 Linux k8s 部署不再内置 Open WebUI（镜像在 ghcr.io，压缩后约 1.5 GB，
/// 国内直连拉取很慢；最终用户也不该为此配代理），因此页面从 302 跳转改为入口页：
/// 给出「打开 AI 对话页」链接 + 自行部署步骤 + 接入百花模型网关的地址。
/// 用户按步骤部署完，点本页按钮即可打开，不需要改代码。
///
/// 地址解析（全部是「浏览器侧视角」，与容器网络无关）：
///   OpenWebUi:BaseUrl（环境变量 OpenWebUi__BaseUrl）显式配置优先；
///   未配置时取 Baihua:PublicBaseUrl 的主机名（k8s ConfigMap 已注入），再加 :8080；
///   再退到当前请求的 Host。写死 localhost 会让局域网其它设备打不开（localhost 指向设备自己）。
/// 模型网关地址：
///   Baihua:PublicBaseUrl（Traefik :80 统一入口）→ /mg/ai/v1；未配置则用当前请求的 scheme+host。
/// </summary>
public static class OpenWebUiLandingPage
{
    public sealed record TestChatRequest(string ModelId);

    /// <summary>Open WebUI 默认监听端口（本页在未配置地址时按它推导）。</summary>
    private const int DefaultPort = 8080;

    private const string Template = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<link rel="icon" type="image/png" href="favicon.png">
<title>AI 对话页</title>
<style>
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
    padding: 24px; background: #f5f6f8; color: #1f2328;
    font-family: -apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
  }
  .card {
    width: 100%; max-width: 760px; background: #fff; border: 1px solid #e3e6ea;
    border-radius: 16px; padding: 28px 28px 24px; box-shadow: 0 8px 28px rgba(0,0,0,.06);
  }
  h1 { margin: 0 0 10px; font-size: 22px; display: flex; align-items: center; gap: 8px; }
  h1 .dot { width: 9px; height: 9px; border-radius: 50%; background: #ff2442; display: inline-block; }
  p { line-height: 1.7; margin: 0 0 14px; }
  .lead { color: #444; font-size: 14.5px; }
  .actions { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin: 18px 0 6px; }
  .btn {
    display: inline-flex; align-items: center; gap: 6px; padding: 11px 20px;
    border-radius: 10px; background: #ff2442; color: #fff; text-decoration: none;
    font-size: 15px; font-weight: 600; transition: filter .15s;
  }
  .btn:hover { filter: brightness(1.08); }
  .addr { color: #6b7280; font-size: 13px; word-break: break-all; }
  .status { font-size: 13.5px; margin: 8px 0 0; min-height: 1.4em; }
  .status.ok { color: #1a7f37; }
  .status.warn { color: #9a6700; }
  .diagnostics { margin-top: 20px; padding: 14px; background: #f6f8fa; border: 1px solid #e3e6ea; border-radius: 10px; }
  .diagnostics-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
  .diagnostics-head strong { font-size: 15px; }
  .diagnostics-head button, .test-button {
    border: 0; border-radius: 8px; padding: 8px 12px; background: #e8ebef; color: inherit;
    cursor: pointer; font: inherit; font-size: 13px;
  }
  .diagnostics-head button:disabled, .test-button:disabled { opacity: .55; cursor: default; }
  .diagnostics-status, .device-status, .test-result { font-size: 13.5px; margin: 10px 0 0; line-height: 1.6; }
  .model-row { margin: 8px 0 0; padding-top: 8px; border-top: 1px solid #e3e6ea; font-size: 13px; }
  .model-row strong { margin-right: 6px; }
  .test-result { white-space: pre-wrap; word-break: break-word; }
  .addr code, code { background: #f0f2f5; border-radius: 5px; padding: 1px 5px; font-size: 12.5px; }
  .hint { margin: 16px 0 0; padding: 10px 12px; background: #f6f8fa; border: 1px solid #e3e6ea; border-radius: 9px; }
  details { margin-top: 20px; border-top: 1px dashed #dfe3e8; padding-top: 16px; }
  summary { cursor: pointer; font-weight: 600; font-size: 14.5px; color: #1f2328; }
  ol { margin: 14px 0 0; padding-left: 20px; }
  ol li { margin-bottom: 14px; line-height: 1.7; font-size: 14px; }
  ol li > ul { margin: 6px 0 0; padding-left: 18px; }
  ol li > ul li { margin-bottom: 4px; font-size: 13.5px; }
  pre {
    background: #0f1115; color: #e6edf3; border-radius: 9px; padding: 12px 14px;
    font-size: 12.5px; line-height: 1.6; overflow-x: auto; margin: 8px 0;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    white-space: pre-wrap; word-break: break-all;
  }
  .muted { color: #6b7280; font-size: 13px; margin: 10px 0 0; }
  a { color: #d61a35; }
  @media (prefers-color-scheme: dark) {
    body { background: #0d1117; color: #e6edf3; }
    .card { background: #161b22; border-color: #30363d; box-shadow: none; }
    .lead, .addr, .muted { color: #9aa4b2; }
    .addr code, code { background: #21262d; }
    .hint { background: #1c2128; border-color: #30363d; }
    .diagnostics { background: #1c2128; border-color: #30363d; }
    .diagnostics-head button, .test-button { background: #30363d; }
    .model-row { border-top-color: #30363d; }
    details { border-top-color: #30363d; }
    summary { color: #e6edf3; }
    .status.ok { color: #3fb950; }
    .status.warn { color: #d29922; }
    a { color: #ff7b8f; }
  }
</style>
</head>
<body>
<main class="card">
  <h1><span class="dot"></span>AI 对话页</h1>
  <p class="lead">
    AI 对话由 <strong>Open WebUI</strong> 提供，它是一个独立应用，百花的部署包<strong>不再内置</strong>它：
    镜像在境外仓库（压缩后约 1.5&nbsp;GB），默认部署时拉取很慢，交给需要的人按需安装更合适。
    部署好之后，点下面的按钮就能打开，本页无需改动。
  </p>
  <div class="actions">
    <a class="btn" href="__BASE_URL__/" target="_blank" rel="noopener">打开 AI 对话页 ↗</a>
    <span class="addr">地址：<code>__BASE_URL__</code></span>
  </div>
  <p class="status" id="ow-status" aria-live="polite"></p>
  <p class="hint muted">
    百花自身的 AI 功能（虚拟师父、知识生成、每日卡片、摘要等）不依赖 Open WebUI，照常可用；
    这里只是缺一个通用聊天界面。
  </p>

  <section class="diagnostics" aria-labelledby="diagnostics-title">
    <div class="diagnostics-head">
      <strong id="diagnostics-title">百花模型连接检测</strong>
      <button id="diagnostics-refresh" type="button">重新检测</button>
    </div>
    <p class="diagnostics-status" id="diagnostics-status" aria-live="polite">正在检测本地模型服务…</p>
    <div id="model-list"></div>
    <p class="device-status" id="device-status"></p>
    <div class="actions">
      <button class="test-button" id="test-local-model" type="button" disabled>测试本地模型</button>
      <span class="muted">仅发送固定短提示词，不会保存到聊天记录。</span>
    </div>
    <p class="test-result" id="test-result" aria-live="polite"></p>
  </section>

  <details id="ow-steps">
    <summary>还没有部署？展开看部署步骤（约 5 分钟）</summary>
    <ol>
      <li><strong>拉取镜像</strong>（体积大，慢就换国内镜像站，内容相同、版本可能略滞后）：
        <pre>docker pull ghcr.io/open-webui/open-webui:main

# 国内镜像站（拉取慢时用这个）
docker pull ghcr.nju.edu.cn/open-webui/open-webui:main</pre>
      </li>
      <li><strong>启动容器</strong>（对话数据存在命名卷 <code>open-webui</code> 里，换镜像不丢历史）：
        <pre>docker run -d --name open-webui --restart unless-stopped \
  -p 8080:8080 \
  -v open-webui:/app/backend/data \
  ghcr.io/open-webui/open-webui:main</pre>
        <ul>
          <li>装在本服务器上最省事：本页按钮默认就指向 <code>__BASE_URL__</code>，装完即可点开。</li>
          <li>本机是 k3s（没有 docker）时，也可以直接 apply 仓库里的可选清单：
            <code>sudo k3s kubectl apply -f k8s/optional/open-webui.yaml</code>
            （同样发布到宿主 :8080；首次部署会预填百花网关和 OVMS 直连；已有数据卷可能需手动添加 OVMS）。</li>
          <li>装在另一台机器也行，见第 5 步。</li>
        </ul>
      </li>
      <li><strong>首次打开创建管理员</strong>：浏览器访问 <code>__BASE_URL__</code>，
        第一个注册的账号就是管理员。</li>
      <li><strong>接入百花模型网关</strong>（这样模型列表里会出现云端和算力池文本模型）：
        进入 <em>管理员设置 → 连接 → OpenAI API</em>，填：
        <pre>基础地址（Base URL）: __GATEWAY_URL__
API Key:             随便填一个非空值（如 baihua）</pre>
        <ul>
          <li>如果百花管理员配置了 <code>BAIHUA_AI_EXTERNAL_TOKEN</code>，API Key 必须填它的值。</li>
          <li>保存后点该页面的「刷新」，模型会自动出现在下拉列表里。</li>
          <li>模型 ID 使用 <code>baihua.</code> 前缀区分百花网关来源。</li>
          <li>百花网关目前只支持纯文本对话（含流式响应）；多模态聊天内容会报错，独立媒体和 embedding 接口尚未实现。</li>
          <li>地址里的主机名必须是 Open WebUI 能访问到的百花地址（通常是局域网 IP）；
            容器里的 <code>localhost</code> 指向容器自己，不能填 localhost。</li>
        </ul>
      </li>
      <li><strong>（可选，k8s 内）添加 OVMS 直连</strong>：如需给本机视觉模型发送图片，
        在「连接 → OpenAI API」中添加一个新的连接，地址填 <code>http://bh-openvino:8000/v3</code>，API Key 留空。
        该地址只在 k8s 集群内可达；模型 ID 带 <code>ovms.</code> 前缀。当前模型支持文本和图片，不提供音频、视频或 embedding。</li>
      <li><strong>装在别的机器 / 换端口时</strong>：把百花 WebUI 的配置
        <code>OpenWebUi:BaseUrl</code>（环境变量 <code>OpenWebUi__BaseUrl</code>）改成
        <code>http://&lt;那台机器的IP&gt;:&lt;端口&gt;</code>，重启 webui 后本页按钮立即指向它。
        当前解析出的地址见页面顶部的「地址」。</li>
      <li><strong>数据与卸载</strong>：
        <pre>docker rm -f open-webui            # 停止并删除容器（对话数据还在卷里）
docker volume rm open-webui         # 连数据一起删（不可恢复，先导出备份再执行）</pre>
      </li>
    </ol>
    <p class="muted">
      官方文档：<a href="https://docs.openwebui.com/" target="_blank" rel="noopener">docs.openwebui.com</a>
      · 镜像仓库：<a href="https://github.com/open-webui/open-webui" target="_blank" rel="noopener">github.com/open-webui/open-webui</a>
    </p>
  </details>
</main>
<script>
(function () {
  var statusEl = document.getElementById('ow-status');
  var stepsEl = document.getElementById('ow-steps');
  var diagnosticsStatusEl = document.getElementById('diagnostics-status');
  var modelListEl = document.getElementById('model-list');
  var deviceStatusEl = document.getElementById('device-status');
  var refreshButton = document.getElementById('diagnostics-refresh');
  var testButton = document.getElementById('test-local-model');
  var testResultEl = document.getElementById('test-result');
  var selectedModelId = null;
  var target = "__BASE_URL__/";
  if (!statusEl || !window.fetch || !window.AbortController) {
    if (statusEl) { statusEl.textContent = ''; }
    return;
  }
  var ctrl = new AbortController();
  var timer = setTimeout(function () { ctrl.abort(); }, 4000);
  // 跨源 no-cors 探测：只要能连上（哪怕返回 401/HTML）就 resolve；DNS/连接失败会 reject。
  fetch(target, { mode: 'no-cors', cache: 'no-store', signal: ctrl.signal })
    .then(function () {
      clearTimeout(timer);
      statusEl.textContent = '✓ 已检测到 Open WebUI，点上面的按钮即可开始对话。';
      statusEl.className = 'status ok';
    })
    .catch(function () {
      clearTimeout(timer);
      statusEl.textContent = '尚未检测到 Open WebUI —— 按下面步骤部署后刷新本页即可。';
      statusEl.className = 'status warn';
      if (stepsEl) { stepsEl.open = true; }
    });

  function appendModelRow(provider, model) {
    var row = document.createElement('p');
    row.className = 'model-row';
    var title = document.createElement('strong');
    title.textContent = model.id;
    var category = provider.tier === 2 ? '本地' : provider.tier === 3 ? '云端' : '其他';
    var status = provider.tier === 2
      ? model.isAvailable === true ? '已加载'
        : model.isAvailable === false ? '未加载'
          : '暂无法确认'
      : '已配置，未主动探测';
    if (!model.isConfigured && model.isAvailable === true) status = '服务端已加载，尚未加入百花配置';
    row.appendChild(title);
    row.appendChild(document.createTextNode('[' + category + '] ' + status));
    modelListEl.appendChild(row);
  }

  async function loadDiagnostics() {
    diagnosticsStatusEl.textContent = '正在检测本地模型服务…';
    diagnosticsStatusEl.className = 'diagnostics-status';
    modelListEl.textContent = '';
    deviceStatusEl.textContent = '';
    testButton.disabled = true;
    selectedModelId = null;
    testResultEl.textContent = '';
    try {
      var response = await fetch('/open-webui/diagnostics', { cache: 'no-store' });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      var payload = await response.json();
      var providers = payload.gateway && payload.gateway.providers ? payload.gateway.providers : [];
      var readyCount = 0;
      providers.forEach(function (provider) {
        var heading = document.createElement('p');
        heading.className = 'model-row';
        var kind = provider.tier === 2 ? '本地模型服务' : provider.tier === 3 ? '云端模型配置' : '模型提供方';
        var health = '未进行连通性检测';
        if (provider.tier === 2) {
          if (!provider.errorCode) health = '服务可达';
          else if (provider.errorCode === 'connection_failed') health = '连接失败';
          else if (provider.errorCode === 'timeout') health = '连接超时';
          else if (provider.errorCode === 'invalid_base_url') health = '地址配置无效';
          else if (provider.errorCode === 'invalid_model_list') health = '模型列表响应异常';
          else if (provider.errorCode.indexOf('http_') === 0) health = '服务返回 ' + provider.errorCode.slice(5);
          else health = '检测异常（' + provider.errorCode + '）';
        }
        heading.textContent = provider.name + ' · ' + kind + ' · ' + health;
        modelListEl.appendChild(heading);
        (provider.models || []).forEach(function (model) {
          appendModelRow(provider, model);
          if (provider.tier === 2 && model.isConfigured && model.isAvailable === true) {
            readyCount++;
            if (!selectedModelId) selectedModelId = model.id;
          }
        });
      });
      diagnosticsStatusEl.textContent = providers.length
        ? readyCount
          ? '检测完成：' + readyCount + ' 个本地模型可用。'
          : '检测完成：没有已加载的本地模型；云端模型状态需在 Open WebUI 中实际试用。'
        : '尚未配置 AI 模型提供方。';
      diagnosticsStatusEl.className = 'diagnostics-status ' + (readyCount ? 'status ok' : 'status warn');
      testButton.disabled = !selectedModelId;

      var capability = payload.capability || {};
      deviceStatusEl.textContent = capability.isIntelGpu
        ? '主机检测到 Intel GPU：' + (capability.gpuName || '名称未知')
          + '。OVMS 未通过模型列表接口报告实际推理设备，请以 OVMS 日志确认是否使用 GPU。'
        : '主机未检测到 Intel GPU，本地推理可能使用 CPU，速度会较慢；实际设备请以 OVMS 日志为准。';
    } catch (error) {
      diagnosticsStatusEl.textContent = '检测失败：' + error.message;
      diagnosticsStatusEl.className = 'diagnostics-status status warn';
    }
  }

  refreshButton.addEventListener('click', loadDiagnostics);
  testButton.addEventListener('click', async function () {
    if (!selectedModelId) return;
    testButton.disabled = true;
    testResultEl.textContent = '正在测试 ' + selectedModelId + '…';
    try {
      var response = await fetch('/open-webui/test-chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ modelId: selectedModelId })
      });
      var result = await response.json();
      if (!response.ok) throw new Error(result.detail || result.error || ('HTTP ' + response.status));
      testResultEl.textContent = '测试成功：' + (result.text || '(模型返回空内容)');
    } catch (error) {
      testResultEl.textContent = '测试失败：' + error.message;
    } finally {
      testButton.disabled = !selectedModelId;
    }
  });
  loadDiagnostics();
})();
</script>
</body>
</html>
""";

    /// <summary>
    /// 解析 Open WebUI 的浏览器侧地址：显式配置 &gt; 百花公开地址的主机名 + :8080 &gt; 当前请求 Host。
    /// </summary>
    public static string ResolveBaseUrl(string? configuredBaseUrl, string? publicBaseUrl, HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
            return configuredBaseUrl.Trim().TrimEnd('/');

        // Baihua:PublicBaseUrl 是「局域网入口」的规范地址（k8s ConfigMap 注入，如 http://192.168.3.9）。
        // 用它推导出的 :8080 对局域网内任何设备都可用；退化到请求 Host 时至少服务器本机能用。
        var host = ExtractHost(publicBaseUrl);
        if (host is null)
            host = string.IsNullOrEmpty(request.Host.Host) ? "localhost" : request.Host.Host;

        return $"http://{host}:{DefaultPort}";
    }

    /// <summary>
    /// 解析 Open WebUI 应填的百花模型网关地址（OpenAI 兼容 /mg/ai/v1）。
    /// 优先用公开入口（Traefik :80），否则用当前请求的 scheme+host。
    /// </summary>
    public static string ResolveGatewayUrl(string? publicBaseUrl, HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            return publicBaseUrl.Trim().TrimEnd('/') + "/mg/ai/v1";

        return $"{request.Scheme}://{request.Host}/mg/ai/v1";
    }

    public static string Render(string baseUrl, string gatewayUrl)
    {
        var encoder = HtmlEncoder.Default;
        return Template
            .Replace("__BASE_URL__", encoder.Encode(baseUrl))
            .Replace("__GATEWAY_URL__", encoder.Encode(gatewayUrl));
    }

    /// <summary>从 URL 里取主机名（含 IPv6 方括号）；解析不出来返回 null。</summary>
    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;

        return uri.Host;
    }
}
