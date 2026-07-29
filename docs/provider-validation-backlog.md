# Provider 真实兼容性待验证清单

## 状态与边界

- 状态：Deferred
- 当前真实 Provider：DeepSeek 官方
- 当前真实平台：`osx-arm64`
- 本清单不代表支持承诺；未激活并取得真实证据的 Provider 不得标记为已支持。
- 共享 `openai-compatible` 适配器和离线 Fixture 通过，不等于具体 Provider、
  套餐、模型或平台已经通过真实兼容性验证。
- M6 Wire 1.1 TestClient 只使用本地 Fake OpenAI-compatible Server 验证动态工具
  回调，没有新增 Provider、模型或平台兼容性声明。

## 已验证基线

| Provider | 平台 | 模型 | 状态 | 证据 |
| --- | --- | --- | --- | --- |
| DeepSeek 官方 | `osx-arm64` | `deepseek-v4-pro` | Passed | [M3 实施计划](superpowers/plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md) |
| DeepSeek 官方 | `osx-arm64` | `deepseek-v4-flash` | Passed | [M3 实施计划](superpowers/plans/2026-07-27-open-cowork-m3-agent-runtime-alpha-implementation-plan.md) |

macOS 验证前运行
[`scripts/setup-deepseek-env-macos.zsh`](../scripts/setup-deepseek-env-macos.zsh)，
然后完全退出并重新打开 Codex，使新进程继承当前登录会话的临时环境变量。

## 后续待验证

| Provider 路径 | 平台 | 模型 | 激活条件 |
| --- | --- | --- | --- |
| DeepSeek 官方 | `win-x64` | `deepseek-v4-pro`、`deepseek-v4-flash` | M10 双平台发布候选验收，或用户提前要求。 |
| 千问 Token Plan | `osx-arm64`、`win-x64` | `qwen3.8-max-preview`、`glm-5.2`、`deepseek-v4-pro`、`deepseek-v4-flash` | 用户提供匹配套餐与区域的专属 Base URL/API Key，并明确要求启动验证。 |
| 其他 OpenAI-compatible Provider | 按发布目标确定 | 激活时冻结精确 Model ID | 用户确认 Provider、模型和平台范围。 |

## 激活规则

1. 先核对 Provider 官方文档中的 Base URL、认证形式、精确 Model ID、套餐和使用限制。
2. 再为本次激活的 Provider 编写最小真实发布测试；不要预先保留 `NotRun` 占位矩阵。
3. 真实测试必须显式运行，不进入默认 `dotnet test`，不保存 Prompt、回答、原始响应
   或 Secret。
4. 每条路径至少验证 `[DONE]`、非空 Content、Finish Reason、Usage、Tokenizer
   对账和 Secret Canary。
5. 通过后更新本清单、对应验收目录和交付证据；失败或未运行不得对外宣称支持。
