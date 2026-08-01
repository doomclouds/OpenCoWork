# Provider 支持与真实兼容性台账

## 状态与边界

- 状态：M9 Planned
- 2026-08-01 用户确认 1.0 Provider 实现收敛为 DeepSeek-only Responses API。
- M9 首发只支持 DeepSeek 官方 `deepseek-v4-flash`。
- `deepseek-v4-pro` 只有在 DeepSeek 官方支持 Responses API 且完成独立真实验证后
  才能激活；“未来会支持”不是当前兼容性声明。
- 千问 Token Plan、其他 Provider 和通用 `openaiCompatible` Chat Completions
  协议路径退出 1.0 目标支持面。
- M9 实现完成前，仓库中既有 OpenAI-compatible/Qwen 路径只是待替换的当前代码，
  不代表新的 1.0 支持承诺。

## 官方协议基线

- 权威范围：[DeepSeek Using the Responses API](https://api-docs.deepseek.com/guides/responses_api/)
  与 [DeepSeek Error Codes](https://api-docs.deepseek.com/quick_start/error_codes/)；
  M9 独立规格和实现必须以实施时的官方文档为准。
- 当前基线只允许 `https://api.deepseek.com`、`deepseek-v4-flash`、无状态请求和
  官方明确标记为 Supported/Partially supported 的参数、输入项、工具、响应字段与
  SSE 事件；未记录能力不从 OpenAI Responses API 推导。
- OpenCoWork 只实现现有运行时需要的官方子集：文本输入、流式文本/Reasoning、
  `function` 工具调用与结果回注、服务端 `web_search`、官方
  `custom/apply_patch`、终态和 Usage；不顺带实现结构化输出或其他未激活能力。
- M9 删除本地 `web.fetch/CoreWebTool`，由 DeepSeek 服务端 `web_search` 承担模型
  联网搜索；模型侧 `file.write` 由 `custom/apply_patch` 取代。路径包含、Authority、
  Approval、原子提交、审计和 Journal 仍由本地 Runtime 保证。
- `previous_response_id`、`conversation`、`store`、`background` 等官方不支持能力
  不进入公共配置；DeepSeek 对不支持参数的静默忽略不能作为兼容策略。

## 历史已验证基线

下列结果只证明 M3 的旧 Chat Completions 实现在对应提交上通过，不证明新的
Responses API 实现或当前 1.0 目标已经完成：

| Provider | 平台 | 模型 | 协议 | 状态 | 证据 |
| --- | --- | --- | --- | --- | --- |
| DeepSeek 官方 | `osx-arm64` | `deepseek-v4-pro` | Chat Completions | Historical Passed | [M3 实施计划](superpowers/plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md) |
| DeepSeek 官方 | `osx-arm64` | `deepseek-v4-flash` | Chat Completions | Historical Passed | [M3 实施计划](superpowers/plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md) |

## M9 目标验证

| Provider | 平台 | 模型 | 协议 | 状态 | 激活/关闭条件 |
| --- | --- | --- | --- | --- | --- |
| DeepSeek 官方 | `osx-arm64` | `deepseek-v4-flash` | Responses API | Planned | M9 独立规格冻结协议后，从 `osx-arm64` 发布目录完成真实冒烟、Function、`web_search`、`custom/apply_patch`、Usage 对账和 Secret Canary。 |
| DeepSeek 官方 | `win-x64` | `deepseek-v4-flash` | Responses API | Planned | M9 独立规格冻结协议后，从 `win-x64` 发布目录完成同等真实证据。 |
| DeepSeek 官方 | `osx-arm64`、`win-x64` | `deepseek-v4-pro` | Responses API | Deferred | DeepSeek 官方明确支持该模型的 Responses API，且用户激活双平台真实验证。 |

## Removed 路径

| Provider 路径 | 1.0 结论 | 处理 |
| --- | --- | --- |
| 千问 Token Plan | Removed | M9 删除 Provider 声明、Tokenizer/Profile 和真实验证入口；旧配置返回稳定迁移诊断。 |
| 其他 OpenAI-compatible Provider | Removed | M9 删除通用协议承诺和动态 Provider 接入；不保留占位兼容矩阵。 |
| DeepSeek Chat Completions | Removed | 历史证据保留，生产实现由 DeepSeek Responses API 取代。 |

## 激活规则

1. M9 设计与实现前重新核对上面的 DeepSeek 官方协议基线；只冻结当时文档明确
   支持且 OpenCoWork 需要的最小子集。
2. 只为已经激活的模型编写最小真实发布测试；不预建其他 Provider 或 Pro 的
   `NotRun` 占位矩阵。
3. 真实测试必须显式运行，不进入默认 `dotnet test`，不保存 Prompt、回答、原始
   响应、Response ID 或 Secret。
4. 每条路径至少验证无 `[DONE]` 的 SSE 流、`response.completed` /
   `response.incomplete` / `response.failed` 终态、非空 Content、Function
   Call/Output、服务端 `web_search`、`custom/apply_patch`、Reasoning、Usage、
   Tokenizer 对账和 Secret Canary。
5. 通过后更新本台账、M9 验收目录和交付证据；失败或未运行不得对外宣称支持。
6. M11 最终发布候选必须在两平台重新执行 `deepseek-v4-flash` Responses API
   真实验收；早期 M3 Chat Completions 结果不能替代。
