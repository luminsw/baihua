# 移动端题库接口连接问题

## 现象

鸿蒙端和安卓端进入题库页时无法显示题库。

## 已观察到的错误

1. 鸿蒙端曾出现：

   ```text
   HTTP 0
   HTTP GET http://192.168.3.9/mg/learn/banks failed: Failed to connect to the server
   ```

2. 安卓端曾出现：

   ```text
   HTTP 403
   Admin API is restricted to local access only.
   Please use the WebUI.
   ```

请求地址为：

```text
http://192.168.3.9/mg/learn/banks
```

## 初步判断

这是服务端连接/访问控制问题，不是题库数据为空，也不是移动端题库 DTO 解析问题。

- HTTP 0：服务地址不可达、服务未启动、端口不通或请求打到了错误的服务进程。
- HTTP 403：请求命中了服务端的局域网管理 API 访问控制，或者运行中的服务端版本没有将 `/mg/learn/` 列入移动端公共路径。
- 当前源码 `services/Baihua.Server/Program.cs` 的公共路径表已包含 `/mg/learn/`，因此需要确认实际部署的服务端是否为最新构建并已重启。

## 建议排查步骤

1. 在服务端所在机器确认 `192.168.3.9:8788` 的监听进程和端口映射。
2. 用 `GET /health` 确认请求实际到达哪个服务实例。
3. 重新发布并重启包含 `/mg/learn/` 公共路径配置的 `Baihua.Server`。
4. 确认移动端请求地址、端口和协议与实际部署地址一致。
5. 部署后再次验证 `GET /mg/learn/banks` 的响应状态及服务端日志。

## 范围说明

官网后续也计划添加题库功能。本问题文档只记录移动端当前连接失败现象，不建议通过客户端永久跳过或屏蔽官网题库能力来解决。
