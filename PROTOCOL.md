# SpyYourDesktop 上报协议 v2

本文档是 **SpyYourDesktop 客户端与 ingest 服务端之间的唯一契约**。
客户端实现在 `Services/UsageIngestService.cs` 与 `Models/IngestModels.cs`。

- 协议版本：`2`
- 传输：HTTP/1.1 或 HTTP/2，`POST`，`Content-Type: application/json; charset=utf-8`
- 时间：ISO 8601 UTC，毫秒精度（如 `2026-09-12T10:05:00.123Z`）
- 字符编码：UTF-8。**非 ASCII 字符以原始 UTF-8 输出，不做 `\uXXXX` 转义**（中文标题与艺术家名很常见，转义会让体积翻倍）。服务端必须能解析两种形式
- 本协议**替换** v1 的扁平淡载荷（`window_title` / `raw.reason` 那一套），v1 端点不再使用

---

## 1. 设计原则

协议要能长期演进，靠的是下面这几条硬规则，而不是某个具体字段。

1. **信封 + 类型化事件。** 顶层只描述"谁、何时、按什么策略发送"。观测内容全部下沉到 `events[]`，新增观测维度 = 新增一个 `type`，顶层 schema 不变。
2. **服务端必须忽略未知字段和未知 `type`**，并通过 `ignored` 数组回话，让客户端能学到。
3. **客户端必须忽略响应里的未知字段。** 服务端加字段不得弄崩老客户端。
4. **客户端只根据 `code` 分支，永不匹配 `message` 文本。** `message` 仅供人阅读，可以随时改文案。
5. **加字段、加 `type`、加 `code` = 非破坏性变更。** 删字段、改字段语义或类型 = 破坏性变更，必须开 `v3`。
6. **时间戳由服务端裁定。** 客户端时钟不可信，排序以服务端的接收时间 + `sequence` 为准。
7. **ID 是不透明字符串。** 服务端不得解析其内容。

### 变更类型速查

| 改动 | 是否破坏性 |
| --- | --- |
| 新增可选字段 | 非破坏性 |
| 新增事件 `type` | 非破坏性 |
| 新增 `code` | 非破坏性 |
| 新增 `trigger` 取值 | 非破坏性 |
| 删除字段 / 事件类型 | 破坏性 |
| 修改已有字段语义或类型 | 破坏性 |
| 修改已有 `code` 的含义 | 破坏性 |

---

## 2. 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `POST` | `/api/v2/ingest` | 上报一批事件 |
| `GET` | `/api/v2/capabilities` | *可选*，能力发现（见 §8） |

客户端地址由配置项 `serverUrl` 指定，必须为 `http` 或 `https`。

### 请求头

| 头 | 必填 | 说明 |
| --- | --- | --- |
| `Content-Type` | 是 | `application/json; charset=utf-8` |
| `X-Protocol-Version` | 是 | `2`。便于服务端在解析请求体之前就路由 |
| `X-App-Version` | 是 | 客户端版本，与 `client.version` 一致 |
| `Authorization` | 上传密钥非空时 | `Bearer <uploadKey>` |

> v1 的 `x-name-key` 头**已废弃**，统一走 `Authorization`。URL 中不得携带密钥。

### 压缩

客户端可以发送 `Content-Encoding: gzip`。服务端**必须支持**，否则大标题场景下请求体会显著膨胀。

---

## 3. 请求体

```json
{
  "protocol_version": 2,
  "client": {
    "name": "spyyourdesktop",
    "version": "1.5.0",
    "platform": "windows",
    "os_version": "10.0.19045.0",
    "capabilities": ["window.activity", "media.playback"]
  },
  "device": { "id": "anyi-desktop" },
  "session": {
    "id": "9f1c2e4a7b8d4f0e9a3c5d6e7f8a9b0c",
    "started_at": "2026-09-12T10:00:00.000Z"
  },
  "sent_at": "2026-09-12T10:05:00.000Z",
  "policy": {
    "privacy_mode": false,
    "sample_interval_ms": 5000,
    "heartbeat_ms": 10000,
    "title_max_chars": 150,
    "title_truncate_chars": 140
  },
  "events": [
    {
      "id": "3a7b1c9d5e2f4a8b9c0d1e2f3a4b5c6d",
      "sequence": 128,
      "type": "window.activity",
      "occurred_at": "2026-09-12T10:04:58.120Z",
      "trigger": "change",
      "data": { "title": "Program.cs - SpyYourDesktop", "app": "devenv", "process_id": 1234 }
    },
    {
      "id": "7d2e4f6a8b0c1d3e5f7a9b1c3d5e7f9a",
      "sequence": 129,
      "type": "media.playback",
      "occurred_at": "2026-09-12T10:05:00.001Z",
      "trigger": "media",
      "data": {
        "title": "夜曲",
        "artist": "周杰伦",
        "album": "十一月的萧邦",
        "status": "playing",
        "source": "Spotify"
      }
    }
  ]
}
```

### 3.1 顶层字段

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `protocol_version` | int | 是 | 固定 `2` |
| `client` | object | 是 | 见 §3.2 |
| `device` | object | 是 | `{ "id": string }`，对应配置中的 `machineId` |
| `session` | object | 是 | 见 §3.4 |
| `sent_at` | timestamp | 是 | 客户端发送时刻 |
| `policy` | object | 是 | 见 §3.5 |
| `events` | array | 是 | 事件列表。**空数组是合法的心跳**，客户端无内容可报时也保持连接 |

### 3.2 `client`

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `name` | string | 是 | 固定 `spyyourdesktop` |
| `version` | string | 是 | 程序集 InformationalVersion |
| `platform` | string | 是 | 固定 `windows` |
| `os_version` | string | 是 | 形如 `10.0.19045.0` |
| `capabilities` | string[] | 是 | 客户端**本配置下**能产生的事件类型。关闭媒体上报时不含 `media.playback` |

### 3.4 `session`

一次监控运行的标识。服务端据此切分"重启过的会话"。

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 每次点击"开始监控"重新生成 |
| `started_at` | timestamp | 是 | 本次会话开始时刻 |

会话内 `sequence` 从 1 重新计数。

### 3.5 `policy`

**每次请求都携带**。没有它就无法区分"用户三小时没动电脑"和"客户端根本没在监控"，
也无法解释标题为什么被截断。

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `privacy_mode` | bool | 是 | 为 `true` 时窗口负载为占位内容，且不含媒体事件 |
| `sample_interval_ms` | int | 是 | 客户端采样间隔，取值 5000–3600000 |
| `heartbeat_ms` | int | 是 | 心跳上限，取值 10000–3600000 |
| `title_max_chars` | int \| null | 否 | 客户端侧标题上限。`null` 或缺失表示不限制 |
| `title_truncate_chars` | int \| null | 否 | 超限时的截断目标长度 |

服务端**不得**把 `policy` 当作可信输入来分配资源，它只是客户端行为说明。

### 3.6 `events[]`

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 不透明唯一标识，**同时是幂等键** |
| `sequence` | int64 | 是 | 会话内从 1 单调递增。有空洞即代表丢包 |
| `type` | string | 是 | 事件类型，见 §4 |
| `occurred_at` | timestamp | 是 | 事件发生时刻。重试时**保持不变** |
| `trigger` | string | 是 | 本事件被发送的原因，见 §5 |
| `data` | object | 否 | 类型化负载，形状由 `type` 决定 |

---

## 4. 事件类型

未知 `type` 必须被服务端**忽略**（而不是报错），并在 `ignored` 中回话。

### 4.1 `window.activity`

前台窗口活动。

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `title` | string | 是 | 窗口标题，已按 `policy` 规整（换行替换为空格、去首尾空白、截断） |
| `app` | string | 是 | 进程名（不含扩展名），可能为空（进程访问被拒时） |
| `process_id` | int | 是 | 前台窗口所属进程 PID |

隐私模式下三者为固定值：`"TA现在不想给你看QAQ"` / `"private mode"` / `0`。

### 4.2 `media.playback`

系统媒体会话（SMTC）信息。仅在 `media` 上报开关开启、且当前存在媒体会话时出现。

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `title` | string | 是 | 曲目标题 |
| `artist` | string | 是 | 艺术家 |
| `album` | string | 是 | 专辑 |
| `status` | string | 是 | `playing` / `paused` / `stopped` / `changing` / `closed`，未知时为空串 |
| `source` | string | 是 | 来源应用，取自 AUMID 的规范化短名（如 `Spotify`） |

> **兼容提示**：`status` 是开放字符串。客户端遇到不认识的 SMTC 状态会原样上报，服务端应把未知值当作"状态未知"处理，不要丢弃整个事件。

---

## 5. 触发原因 `trigger`

`trigger` 描述**该事件为什么被发送**，与事件一一对应。同一批次内不同事件可以有不同的 `trigger`。

| 取值 | 含义 |
| --- | --- |
| `startup` | 监控启动时的首次上报 |
| `change` | 前台窗口标题发生变化 |
| `media` | 媒体曲目或播放状态发生变化 |
| `heartbeat` | 到达心跳上限，为证明存活而发送 |
| `manual` | 用户操作触发（如切换隐私模式后立即上报） |

新增取值是非破坏性变更。客户端按"变化驱动"发送：标题或媒体内容不变时不产生请求，只由心跳兜底。

---

## 6. 响应

### 6.1 成功（HTTP 2xx）

```json
{
  "protocol_version": 2,
  "server_time": "2026-09-12T10:05:00.150Z",
  "accepted": ["3a7b1c9d5e2f4a8b9c0d1e2f3a4b5c6d"],
  "rejected": [
    {
      "id": "7d2e4f6a8b0c1d3e5f7a9b1c3d5e7f9a",
      "code": "FIELD_TOO_LONG",
      "message": "album exceeds 200 characters",
      "retryable": false,
      "details": { "field": "album", "limit": 200, "length": 342 }
    }
  ],
  "ignored": [
    { "type": "clipboard.content", "code": "UNSUPPORTED_TYPE" }
  ],
  "pacing": { "min_interval_ms": 5000, "next_upload_after_ms": 8000 }
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `protocol_version` | int | 是 | 服务端实现的版本，用于及早发现版本错配 |
| `server_time` | timestamp | 否 | 服务端时刻，供客户端校正 |
| `accepted` | string[] | 否 | 已接受的事件 `id` |
| `rejected` | object[] | 否 | 事件级拒绝，见 §6.2 |
| `ignored` | object[] | 否 | 因类型未知等原因未处理的事件，见 §6.3 |
| `pacing` | object | 否 | 服务端主动限速，见 §6.4 |

**部分成功是常态。** 只要 HTTP 是 2xx，请求本身就是成功的；单个事件被拒绝不等于整批失败。
不要因为一个事件不合法就让整个客户端停摆。

返回 `204 No Content` 或空响应体是合法的，表示整批接受且无需逐事件回执。

### 6.2 `rejected[]`

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 被拒事件的 `id` |
| `code` | string | 是 | 错误码，见 §7.2 |
| `message` | string | 否 | 人类可读说明，**客户端不得依赖其内容** |
| `retryable` | bool | 是 | 重发同一内容是否可能成功 |
| `details` | object | 否 | 机器可读的补充信息 |

`details` 是约定的弱结构化字段。已定义的键：

| `code` | `details` 键 |
| --- | --- |
| `FIELD_TOO_LONG` | `field`（字段名）、`limit`（上限）、`length`（实际长度） |
| `INVALID_FIELD` | `field`、`reason` |
| `DUPLICATE` | 无 |

客户端会消费 `FIELD_TOO_LONG` 的 `details.limit`：若 `field` 为 `title`，则在后续请求中主动把标题截断到该上限，因此服务端**应**提供该值。

### 6.3 `ignored[]`

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 否 | 事件 `id`，若可识别 |
| `type` | string | 是 | 事件类型 |
| `code` | string | 是 | 通常为 `UNSUPPORTED_TYPE` |

客户端收到 `UNSUPPORTED_TYPE` 后会**在该会话内停止发送该类型**，避免每轮都产生噪声。

### 6.4 `pacing`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `min_interval_ms` | int | 服务端期望的最小上报间隔 |
| `next_upload_after_ms` | int | 距下次上报建议等待的毫秒数 |

客户端会把 `next_upload_after_ms` 作为**执行间隔的下限**（取 `max(采样间隔, pacing)`）。
这让服务端可以主动降频，而不必等客户端撞上 429。客户端会将其钳制在 0–60000 ms。

---

## 7. 错误

### 7.1 请求级失败

非 2xx 响应体使用 Problem Details（对齐 RFC 9457）：

```json
{
  "type": "https://example.com/problems/rate-limited",
  "title": "Too many requests",
  "status": 429,
  "code": "RATE_LIMITED",
  "retryable": true,
  "retry_after_ms": 1200,
  "detail": "min_interval_ms=5000, elapsed_ms=3800"
}
```

若响应体不是合法 JSON，客户端按 HTTP 状态码回退推导 `code` 与 `retryable`。

客户端行为：

| `code` | 客户端行为 |
| --- | --- |
| `RATE_LIMITED` | 按 `retry_after_ms` 退避（钳制在 300–5000 ms）后继续 |
| `SERVER_ERROR` | 指数退避重试，最多 3 次；仍失败则停止监控并提示用户 |
| 其他 | 停止监控并提示用户 |

`retryable` 为 `true` 时客户端会重试。服务端**应**显式给出该字段，缺失时客户端按
`429` 与 `5xx` 推断。

### 7.2 错误码表

请求级（`IngestProblem.code`）：

| `code` | 典型状态码 | `retryable` | 含义 |
| --- | --- | --- | --- |
| `UNAUTHORIZED` | 401 | 否 | 密钥缺失或无效 |
| `FORBIDDEN` | 403 | 否 | 密钥无权访问该设备 |
| `RATE_LIMITED` | 429 | 是 | 超过最小上报间隔 |
| `PROTOCOL_UNSUPPORTED` | 400 | 否 | 服务端不支持该协议版本 |
| `PAYLOAD_TOO_LARGE` | 413 | 否 | 请求体超出上限 |
| `MALFORMED_REQUEST` | 400 | 否 | 请求体无法解析 |
| `SERVER_ERROR` | 5xx | 是 | 服务端内部错误 |

事件级（`rejected[].code`）：

| `code` | `retryable` | 含义 |
| --- | --- | --- |
| `FIELD_TOO_LONG` | 否 | 某字段超长，见 `details` |
| `INVALID_FIELD` | 否 | 字段值不合法 |
| `UNSUPPORTED_TYPE` | 否 | 该事件类型不被接受 |
| `DUPLICATE` | 否 | `id` 已处理过（幂等命中，不算错误） |

---

## 8. 能力发现（可选）

```
GET /api/v2/capabilities
```

```json
{
  "protocol_version": 2,
  "versions": [2],
  "event_types": ["window.activity", "media.playback"],
  "limits": {
    "max_events_per_request": 64,
    "max_request_bytes": 1048576,
    "min_interval_ms": 5000,
    "max_title_chars": 512
  }
}
```

**当前客户端不使用该端点。** 它通过 `client.capabilities` 声明自身能力，并依赖
`ignored` 回执做反应式收敛。这样即使该端点不可用或未实现，客户端依然能正确工作。

服务端**可以**实现它，供未来的客户端做主动协商。实现时两侧都应保持"缺失即降级"的容忍度。

---

## 9. 幂等与重试

- `id` 是幂等键。服务端对同一 `id` 应返回 `accepted`（或 `DUPLICATE` 拒绝），不得重复入库。
- 重试时客户端**不修改** `occurred_at`，只更新 `sent_at`。
- `sequence` 用于检测丢包。服务端发现空洞时可以提示客户端补发，但**不得**将其当作幂等依据。
- 客户端重试上限为 3 次指数退避（500 / 1000 / 2000 ms）。

---

## 10. 隐私

- **隐私模式优先于一切。** `policy.privacy_mode = true` 时，窗口负载为占位内容，且**不产生**媒体事件。客户端在隐私模式下不会读取 SMTC。
- 媒体信息属于高敏感个人数据。服务端**必须**提供按设备清除媒体事件的能力。
- `title` 与媒体字段在客户端侧已做换行替换与长度截断。
- 密钥只经 `Authorization` 头传输，不得出现在 URL、日志或错误消息中。

---

## 11. 客户端实现映射

| 协议概念 | 代码位置 |
| --- | --- |
| 常量、错误码、请求/响应模型 | `Models/IngestModels.cs` |
| 请求组装、发送、响应解析 | `Services/UsageIngestService.cs` |
| 会话、序号、触发判定、`FIELD_TOO_LONG` 自适应、pacing 应用 | `Services/MonitoringService.cs` |
| SMTC 读取 | `Services/MediaSessionService.cs` |
| 上报开关与配置持久化 | `ViewModels/MainViewModel.cs`、`Models/AppConfig.cs` |

### 当前实现的选择

- 客户端**每次只发一个事件**（一个 `window.activity`，可选带一个 `media.playback`）。协议支持批量，客户端尚未使用。
- 客户端**不做**请求体压缩，尚未需要。
- 客户端**不调用** §8 的能力发现端点。

这些都属于实现细节，不影响协议兼容性；服务端不应假定请求中只有一个事件。
