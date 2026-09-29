# MiniOCR

基于 [huiyuanai709/SimdPaddleOCR](https://github.com/huiyuanai709/SimdPaddleOCR)（上游 [sdcb/SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR)，子模块跟踪 `main`，当前钉在 `6aae0ad`）的 **Native AOT** PDF OCR HTTP API（**.NET 11 RC / `net11.0`**）。

从 URL 并发下载 PDF（≤300 MB），按页流式栅格化 + OCR（最多约 2000 页），返回每页文本、耗时，以及 **公司名 / 人名** JSON。

**OCR 模式（`ocr.mode`）：**

- **`local`（默认）**：本地 PP-OCRv6 **ChineseV6Tiny**；实体抽取优先 OpenAI 兼容 LLM NER，可回退启发式。
- **`llm`**：跳过本地 Paddle 模型加载；将每页 JPEG 以 `image_url` data URL 发给多模态 Chat Completions，一次调用尽量直接返回竞赛形状的 B04/B06 `ruleList`（可配高并发，I/O 密集）。
- **`wechat`（实验，仅 Windows x64）**：调用本机微信自带 OCR 插件做对比。栅格、按 10 个非空页分组的 LLM NER、协议输出、丢掉空白页都与 `local` 相同。**不要**拿它当竞赛服务端：接口是非官方逆向，有 ToS 风险。详见下方「微信 OCR」。

多机 OCR 默认**关闭**。打开 `cluster` 后，同一份二进制可以当协调节点和/或工人节点，把页级 OCR 拉到多台机器上（协调节点自己也算一台）。见下文「分布式 OCR」。

## 竞赛协议（serviceUrl）

平台**无登录/无 token**调用本服务；填入竞赛后台的 **serviceUrl** 推荐：

```text
https://<你的公网主机>:5080/challenge
```

也支持把 serviceUrl 填成根路径 `https://<host>:5080/`（`POST /` 与 `POST /challenge` 等价）。调试用同步接口 `POST /ocr` 使用**与回调相同的 JSON 形状**（见下）；`GET /health` 用于探活。

### 平台 → 本服务（须快速返回 200）

`POST {serviceUrl}`，`Content-Type: application/json`：

```json
{
  "teamId": 123,
  "key": "回调凭证（无横线 uuid）",
  "callbackUrl": "https://agw.yzwqa.cn/ifs/cloudsound/api/votenologin/challenge/callback",
  "files": [
    { "fileId": "f1", "url": "https://...pdf" }
  ]
}
```

成功受理时立即返回 HTTP 200：

```json
{ "ok": true }
```

随后在后台异步：按 `files[]` 下载 PDF → OCR → 按页抽取人员/公司 → `POST callbackUrl`。支持一次请求多个文件。不要在请求线程上跑完整 OCR。

### 本服务 → 平台回调

```json
{
  "teamId": 123,
  "key": "与下发时相同的 key",
  "result": [
    {
      "fileId": "f1",
      "pages": [
        {
          "page": 1,
          "ruleList": [
            {
              "ruleCode": "B04",
              "ruleName": "人员名称",
              "ruleItemList": [
                {
                  "personName": "游春燕",
                  "count": 1,
                  "originText": ["法定代表人或其委托代理人：　游春燕（签字或盖章）"]
                }
              ]
            },
            {
              "ruleCode": "B06",
              "ruleName": "公司名称",
              "ruleItemList": [
                {
                  "companyName": "成都交子商圈物业服务有限公司",
                  "count": 1,
                  "originText": ["正本成都交子商圈物业服务有限公司2026年度一标段（写字楼、"]
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

规则约定：

| ruleCode | ruleName | 条目字段 |
| --- | --- | --- |
| B04 | 人员名称 | `personName` |
| B06 | 公司名称 | `companyName` |

- `count`：该名字在**该页**出现次数  
- `originText`：每次命中附近截取的原文片段数组，每段 **10–100 字**（居中扩窗，边界不足则向另一侧借）  
- 实体来自 LLM NER（若已配置）或启发式；若 LLM 只给名字，会回扫页文本统计 `count` 并生成 `originText`  
- 回调失败会重试数次；日志只记录 `keyPresent`，不打印完整 key  

吞吐：后台队列 + 最多 2 路并行 OCR 任务，避免在约 5 QPM 下把进程打崩。目标场景：约 2000 页 PDF、准确率 90%+、≤5 分钟/份（视机器与 DPI/LLM 而定）。

本地冒烟示例（假回调监听）：

```bash
# 终端 A：假回调（9099）
python3 -c '
from http.server import BaseHTTPRequestHandler, HTTPServer
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n=int(self.headers.get("Content-Length",0)); body=self.rfile.read(n)
        print(body.decode()[:2000]); self.send_response(200); self.end_headers(); self.wfile.write(b"{\"ok\":true}")
    def log_message(self,*a): pass
HTTPServer(("127.0.0.1",9099),H).serve_forever()
'

# 终端 B：静态 PDF
python3 -m http.server 8000 --directory samples

# 终端 C：下发挑战（服务已在 5080）
curl -sS -X POST http://127.0.0.1:5080/challenge \
  -H "Content-Type: application/json" \
  -d "{\"teamId\":123,\"key\":\"testkey\",\"callbackUrl\":\"http://127.0.0.1:9099/cb\",\"files\":[{\"fileId\":\"f1\",\"url\":\"http://127.0.0.1:8000/sample-multipage.pdf\"}]}"
```

## 环境要求

- 目标平台：Windows / Linux / macOS（x64 与 ARM64）；本仓库 CI 产出多平台 Native AOT 包
- [.NET 11 RC SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- Native AOT 需要本机 C 工具链（`gcc` / `clang` + zlib 等）
- **x64** 两档：默认 **AVX2**（通用）与 **AVX-512 高档**（见下方「SIMD 指令集档位」）；**ARM64** 不设置档位（基线已含 NEON）

### 安装 .NET 11 RC（Linux）

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
dotnet --list-sdks
```

## AOT 发布（关键，竞赛默认）

```bash
git clone --recurse-submodules https://github.com/huiyuanai709/miniocr.git
cd miniocr
# 已有工作区缺子模块时：git submodule update --init --recursive
dotnet publish -c Release -r linux-x64 -o ./artifacts/linux-x64
# 换档位（默认 avx2）：加 -p:IlcInstructionSet=avx512 / avx512v2 / avxvnni …
```

产物目录示例：

| 文件 | 说明 |
| --- | --- |
| `MiniOcr` | Native AOT 可执行文件（linux-x64 约 ~25–26 MB） |
| `libSkiaSharp.so` | Skia 原生库（约 ~12 MB；Windows/macOS 为 `.dll` / `.dylib`） |
| `libpdfium.so` | PDFium 原生库（约 ~7 MB；同理换后缀） |

**这不是「一个文件就能跑」。** 必须把可执行文件与同目录下的原生库一起分发；缺 `.so` / `.dll` / `.dylib` 会在栅格化/位图阶段失败。

### 为什么 AOT 做不到真正单文件？

- Native AOT 产出的是**原生可执行文件**，通过动态加载（P/Invoke）调用 SkiaSharp / PDFium 的共享库。
- `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` **只对非 AOT** 生效；与 `PublishAot=true` 同时设置时会被**静默忽略**（[dotnet/runtime#117986](https://github.com/dotnet/runtime/discussions/117986)、[dotnet/sdk#49995](https://github.com/dotnet/sdk/issues/49995)），`.so` 仍会落在 exe 旁。
- 把原生库**静态链接**进 AOT 二进制需要平台对应的 `.a` / `.lib` 静态库 + `DirectPInvoke` / `NativeLibrary` 配置。官方 SkiaSharp / PDFtoImage NuGet **不提供** Linux/Windows 桌面静态包（仅 WASM 等场景有静态资产），自行编译 Skia + PDFium 静态库超出本仓库范围。
- 因此：**竞赛默认 = AOT 速度（AVX2 on x64）+ 旁路原生库**；需要「拷一个文件就能跑」请用下方可选单文件模式（非 AOT）。

### 运行 AOT 二进制

```bash
cd artifacts/linux-x64
./MiniOcr --urls http://0.0.0.0:5080
```

或非 AOT 开发模式：

```bash
dotnet run -c Release --urls http://127.0.0.1:5080
```

## 可选：真正单文件包（非 AOT）

若只想分发**一个**可执行文件（首次运行会把原生库解压到 `$HOME/.net/MiniOcr/<hash>/`），使用属性 `MiniOcrSingleFile=true`（内部关闭 AOT，打开 `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` + 压缩）：

```bash
dotnet publish -c Release -r linux-x64 -o ./artifacts/linux-x64-singlefile \
  -p:MiniOcrSingleFile=true
```

| 项 | AOT（默认） | 单文件（`MiniOcrSingleFile=true`） |
| --- | --- | --- |
| 分发形态 | `MiniOcr` + `libSkiaSharp.*` + `libpdfium.*` | **仅** `MiniOcr` 一个文件 |
| 体积（linux-x64，本机实测） | exe ~26 MB + so ~19 MB | 压缩后约 **~29 MB** |
| 运行时 | Native AOT | 自包含 JIT（.NET 11） |
| x64 SIMD 内核 | 由 `IlcInstructionSet` **静态**决定（档位见下） | **运行时自动探测**：AVX2 → AVX-512 → VNNI 都会亮 |
| 竞赛吞吐 | **高**（AOT + 选定档位，竞赛主包） | 较低（JIT；勿作竞赛主包） |
| 首次启动 | 直接跑 | 解压原生库到 `~/.net/MiniOcr/` |

运行：

```bash
cd artifacts/linux-x64-singlefile
./MiniOcr --urls http://0.0.0.0:5080
```

本机冒烟（2026-09-21 CST）：把单独的 `MiniOcr` 拷到空目录启动 → `GET /health` 200 → `POST /ocr` 5 页样例约 1.4 s 返回竞赛形状 JSON；目录旁**无** `.so`。

### 配置文件（AppData / Application Support / XDG）

**解析优先级：**

1. 环境变量 `MINIOCR_CONFIG_PATH`（指向具体文件）
2. 若候选路径中**已有文件**，直接读取该文件（不移动、不覆盖）
3. 否则在**规范创建位置**写入示例 `config.json`

| 平台 | 候选读取路径 | 缺失时规范创建位置 |
| --- | --- | --- |
| Windows | `%APPDATA%\MiniOcr\config.json`（`ApplicationData` 为空时回退 `%USERPROFILE%\AppData\Roaming\MiniOcr\config.json`） | 同左（ApplicationData / Roaming） |
| macOS | **主路径** `~/Library/Application Support/MiniOcr/config.json`；**兼容** `~/.config/MiniOcr/config.json`（旧文档误写 XDG 时用户可能放这里） | `~/Library/Application Support/MiniOcr/config.json` |
| Linux | `~/.config/MiniOcr/config.json`（以及若与 `ApplicationData` 不同则一并检查） | `~/.config/MiniOcr/config.json` |

> **macOS 注意：** .NET 的 `SpecialFolder.ApplicationData` 对应 `~/Library/Application Support`，**不是** `~/.config`。请把配置放在 Application Support；若你已放在 `~/.config/MiniOcr/config.json`，程序也会读到（不改动原文件）。`ApplicationData` 为空的启动上下文会回退到 `$HOME` 推导路径，避免相对路径 `MiniOcr/config.json` 静默失效。

启动日志与 `GET /health` 会打印**绝对** `configPath`、`configFileExisted`、`configPathSource`（`env` / `existing` / `canonical`）、`llmUsable`、`llmApiKey`（仅 `(set)` / `(empty)`）。

示例内容：

```json
{
  "llm": {
    "enabled": true,
    "baseUrl": "https://api.openai.com",
    "apiKey": "",
    "model": "gpt-4o-mini",
    "timeoutSeconds": 120,
    "maxCharsPerRequest": 300000,
    "pagesPerRequest": 10,
    "maxConcurrency": 8,
    "ocrConcurrency": 32,
    "ocrMaxCharsHint": 8000,
    "ocrJpegQuality": 70,
    "thinking": false,
    "fallbackToHeuristics": false
  },
  "ocr": {
    "mode": "local",
    "dpi": 96,
    "engines": null,
    "lineWorkers": null,
    "detThreads": null,
    "rasterWorkers": null,
    "useCls": false,
    "autoScaleFromCpu": true,
    "wechatOcrPath": "",
    "wechatDir": "",
    "wechatInstances": null,
    "wechatFallbackToLocal": true,
    "wechatConnectTimeoutSeconds": 20,
    "wechatRequestTimeoutSeconds": 60
  }
}
```

**优先级：**

- OCR：环境变量 `MINIOCR_*` **覆盖** 文件；文件中 `null` / 未写且 `autoScaleFromCpu: true` 时按 CPU 核数自动推算。`MINIOCR_OCR_MODE` 覆盖 `ocr.mode`。
- LLM：主要读配置文件；可用 `MINIOCR_LLM_API_KEY` / `MINIOCR_LLM_BASE_URL` / `MINIOCR_LLM_MODEL` / `MINIOCR_LLM_MAX_CONCURRENCY` / `MINIOCR_LLM_PAGES_PER_REQUEST` / `MINIOCR_LLM_OCR_CONCURRENCY` / `MINIOCR_LLM_OCR_JPEG_QUALITY` / `MINIOCR_LLM_THINKING` 覆盖。也可用 `MINIOCR_CONFIG_PATH` 指定配置文件。**不会**把 `apiKey` 打进日志（仅显示 `(set)` / `(empty)`）。

#### OCR 模式：`local` vs `llm`（视觉 OCR）

| `ocr.mode` | 行为 | 何时用 |
| --- | --- | --- |
| `local`（默认） | 加载 ChineseV6Tiny / `PaddleOcrAll`；栅格后本地 OCR；可选 LLM **文本** NER | 离线、控成本、低延迟本机推理 |
| `llm` | **不加载**本地 Paddle 模型（更快启动、更省 RAM）；栅格→JPEG→多模态 Chat Completions（与视觉 **流水线重叠**） | 有视觉模型配额、希望直接出 B04/B06 |
| `wechat` | **不加载** Paddle；栅格后把 PNG 交给微信 OCR 插件；文本仍走同一套 LLM NER | 仅在自己的 Windows 笔记本上和 `local` 比速度/文本。非 Windows 或找不到插件时，与 `llm` 一样告警并回退 `local`（`wechatFallbackToLocal: false` 则直接退出） |

设置方式：

```json
"ocr": { "mode": "llm" }
```

或：`export MINIOCR_OCR_MODE=llm`。未在配置/`MINIOCR_DPI` 中显式设置 DPI 时，`llm` 默认 **72**（`local` 仍为 **96**）；需要更高清晰度可设 `"dpi": 96` 或 `150`。

**`llm` 模式要求** `llm.enabled` + 非空 `apiKey`（及 `baseUrl` / `model`）。若缺失，启动时会 **明确告警并回退到 `local`**，避免服务起不来。

**为何不再有「下载后空等 ~30s」：** 旧实现会先把**全部页**栅格+JPEG 编码完才发第一张视觉请求。现在与 `local` 一样用有界 Channel：**每页** rasterize→JPEG 后立刻交给视觉 worker；位图不攒全本，JPEG 队列约 `2×ocrConcurrency`。日志会打出 `download done` → `first page JPEG ready` → `first vision request` 的相对时间戳，便于确认重叠。默认 `rasterWorkers=min(8,cores)`（视觉 I/O 时 CPU 留给 PDFium），`llm.ocrJpegQuality` 默认 **70**（可用 `MINIOCR_LLM_OCR_JPEG_QUALITY` 覆盖）。

**视觉请求形态（OpenAI 兼容）：** `POST {baseUrl}/v1/chat/completions`，`messages` 含 `image_url`（`data:image/jpeg;base64,...`），提示词要求返回：

```json
{"text":"整页纯文本","ruleList":[{"ruleCode":"B04","ruleName":"人员名称","ruleItemList":[{"personName":"...","count":1,"originText":["10-100字摘录"]}]},{"ruleCode":"B06", "..."}]}
```

页级并发：`llm.ocrConcurrency`（默认 **32**，范围 **1–256**）；环境变量 `MINIOCR_LLM_OCR_CONCURRENCY` 可覆盖。视觉调用是 **I/O 密集**，可远高于本地引擎数，但务必注意提供商 **RPM / TPM / 费用**（2000 页 × 高并发 ≈ 大量计费与 429 风险）。

**模型建议：**

| 提供商 | 示例 model | 备注 |
| --- | --- | --- |
| OpenAI | `gpt-4o` / `gpt-4o-mini` | 兼容性好；mini 更便宜 |
| 通义 / DashScope 兼容 | `qwen-vl-max` / `qwen-vl-plus` 等 | 中文文档通常较强 |
| 其他 OpenAI 兼容网关 | 带视觉的 chat 模型 | `baseUrl` 指到网关根（无 `/v1` 后缀） |

`llm.ocrMaxCharsHint`（默认 8000）写入提示词，限制模型返回的页文本长度。

#### 微信 OCR（`ocr.mode=wechat`，实验，仅 Windows x64）

用微信自带 OCR 插件和 ChineseV6Tiny 比同一份 PDF 的耗时与文本。栅格（PDFtoImage）、每 10 个非空页一组的 LLM NER、竞赛协议、丢掉空白页都不变。

这是社区逆向的 mmmojo IPC（参考 [swigger/wechat-ocr](https://github.com/swigger/wechat-ocr)、[EEEEhex/QQImpl](https://github.com/EEEEhex/QQImpl)），**不是**微信公开 API。只适合在你自己的电脑上对比，**不要**部署到竞赛服务器（ToS 风险）。本仓库不附带微信文件；插件必须已经装在本机。

集成方式是**纯托管客户端**：运行时从微信目录加载 `mmmojo_64.dll`，手写 3.9 / 4.x 的 protobuf。没有额外的 `wcocr.dll`，因此 linux / osx / arm 的 Native AOT 发布不用编 C++。上游 C API `wechat_ocr` 是进程内单例，一个 `WeChatOCR` 进程也通常是单线程的，所以并发靠多开进程（`ocr.wechatInstances`），每个进程同时只识别一页。

| 微信 | 插件 | 安装目录（里面要有 `mmmojo_64.dll`） |
| --- | --- | --- |
| 3.9.x | `%APPDATA%\Tencent\WeChat\XPlugin\Plugins\WeChatOCR\<ver>\extracted\WeChatOCR.exe` | 常见 `C:\Program Files (x86)\Tencent\WeChat\[3.9.x.x]` |
| 4.x | `%APPDATA%\Tencent\xwechat\XPlugin\plugins\WeChatOcr\<ver>\extracted\wxocr.dll` | 常见 `C:\Program Files\Tencent\Weixin\<ver>`，旁边的上一级有 `weixin.exe` |

路径留空会按这些位置和卸载注册表自动找，并在启动日志里打印候选和最终选中的路径。非 Windows，或找不到插件 / 握手失败：默认打警告并回退 `local`（和 `llm` 缺 key 一样）。`wechatFallbackToLocal: false` 或 `MINIOCR_WECHAT_FALLBACK=0` 则直接退出。

在 Windows 上对比同一 PDF（单进程、逐页，结果写到当前目录 `wechat-vs-local.txt`）：

```powershell
.\MiniOcr.exe --compare "C:\Users\mafuz\OneDrive\Desktop\你的文件.pdf" --pages 5 --dpi 96
```

本机调试本地文件（Unicode 路径）：`POST /ocr` 的 `path`，或 `POST /ocr/upload` 的 multipart。加 `?verbose=1` 会返回每页文本和 ms，而不是竞赛回调形状。PowerShell / curl 示例见文末。

#### 配置 OpenAI / 兼容接口（DeepSeek、Azure、本地）

1. 编辑上述 `config.json`，填入 `llm.apiKey`，按需改 `baseUrl` 与 `model`。
2. `baseUrl` 不要带 `/v1/...` 后缀；客户端会请求 `{baseUrl}/v1/chat/completions`。
3. 示例：
   - OpenAI：`https://api.openai.com` + `gpt-4o-mini`（视觉 OCR 建议 `gpt-4o` / `gpt-4o-mini`）
   - 通义视觉：按网关文档设置 `baseUrl` + `qwen-vl-*`
   - DeepSeek：`https://api.deepseek.com` + 对应模型（若无视觉则仅适合 `local` + 文本 NER）。**Flash/v4 默认开思考**，请保持 `"thinking": false` 以关闭（见上）
   - 本地（如 Ollama 兼容层）：`http://127.0.0.1:11434` + 你的视觉模型名
4. 或仅用环境变量：`export MINIOCR_LLM_API_KEY=sk-...`（其余仍可读文件）。
5. **文本 NER 并发**：`llm.maxConcurrency`（默认 **8**，范围 1–32）用于 `local` 模式下的 Chat Completions 批次；`MINIOCR_LLM_MAX_CONCURRENCY` 可覆盖。
6. **文本 NER 分页**：`llm.pagesPerRequest`（默认 **10**，范围 1–2000）表示每个请求包含的**非空** OCR 页数；`MINIOCR_LLM_PAGES_PER_REQUEST` 可覆盖。空白页不占名额、不发请求。分组是连续、互不重叠的非空页。
7. **视觉 OCR 并发**：`llm.ocrConcurrency`（默认 **32**，1–256）；`MINIOCR_LLM_OCR_CONCURRENCY` 可覆盖。调高可缩短墙钟时间，但请留意 **费率与限流**。
8. **视觉 JPEG 质量**：`llm.ocrJpegQuality`（默认 **70**，40–95）；`MINIOCR_LLM_OCR_JPEG_QUALITY` 可覆盖。
9. **`maxCharsPerRequest`（安全上限）**：默认 **300000**（钳制 1000–2_000_000）。分组以 `pagesPerRequest` 为准；若下一页会让当前组超过该字符数，则提前拆组。单页超限时截断后单独发送。
10. **`thinking`（DeepSeek 思考模式）**：DeepSeek Flash / v4 等模型 **默认开启思考**，会拖慢 NER/OCR。本项目默认 **`thinking: false`（关闭）**，请求体会显式发送：
   ```json
   "thinking": { "type": "disabled" }
   ```
   需要开启时设 `"thinking": true` 或 `"enabled"`（亦可 `MINIOCR_LLM_THINKING=1|true|enabled`），将发送 `{ "type": "enabled" }`。配置接受布尔或字符串：`false` / `"disabled"` → disabled；`true` / `"enabled"` → enabled。文本 NER 与视觉 OCR 均会带上该字段。
11. `local` 模式下 LLM **未启用 / 无 key** 时：仅当 `fallbackToHeuristics: true` 才用启发式 NER（默认 **false** → `entities` 为空）。**一旦调用了 LLM NER**（成功为空或失败），**绝不**再静默回退启发式——记错误日志并返回空实体。`ocr.mode=llm` 视觉路径同样只用结构化视觉输出，不用启发式 invent 实体。

### 吞吐旋钮（文件 + 环境变量 / 请求）

| 变量 | 文件字段 | 默认（auto-scale，约 8 核） | 说明 |
| --- | --- | ---: | --- |
| `MINIOCR_OCR_MODE` | `ocr.mode` | **local** | `local`（Paddle）、`llm`（视觉）或 `wechat`（Windows 微信插件） |
| `MINIOCR_WECHAT_OCR_PATH` | `ocr.wechatOcrPath` | 空（自动） | `WeChatOCR.exe`（3.9）或 `wxocr.dll`（4.x）的完整路径 |
| `MINIOCR_WECHAT_DIR` | `ocr.wechatDir` | 空（自动） | 含 `mmmojo_64.dll` 的微信版本目录 |
| `MINIOCR_WECHAT_INSTANCES` | `ocr.wechatInstances` | `Clamp(核数/4, 1, 3)` | 微信 OCR **进程**数。每个进程同时只跑一页 |
| `MINIOCR_WECHAT_FALLBACK` | `ocr.wechatFallbackToLocal` | **true** | `0/false` 时找不到插件就退出，不回退 Paddle |
| `MINIOCR_LLM_PAGES_PER_REQUEST` | `llm.pagesPerRequest` | **10**（1–2000） | `local` 文本 NER：每个请求的非空页数 |
| `MINIOCR_LLM_OCR_CONCURRENCY` | `llm.ocrConcurrency` | **32**（1–256） | `ocr.mode=llm` 时页级视觉并发 |
| `MINIOCR_LLM_OCR_JPEG_QUALITY` | `llm.ocrJpegQuality` | **70**（40–95） | `ocr.mode=llm` 时页图 JPEG 质量（更低=更快编码/更小上传） |
| `MINIOCR_LLM_THINKING` | `llm.thinking` | **false**（disabled） | DeepSeek 思考模式；`0/1/false/true/disabled/enabled`；默认关闭并显式发送 `thinking.type=disabled` |
| `MINIOCR_ENGINES` | `ocr.engines` | **4**（`Clamp(cores/2, 1, min(16,cores))`） | 页级并行 `PaddleOcrAll` 实例数（仅 local） |
| `MINIOCR_DPI` | `ocr.dpi` | **96**（local）/ **72**（llm，未显式设置时） | 栅格化 DPI（也可在 JSON/`?dpi=` 覆盖） |
| `MINIOCR_LINE_WORKERS` | `ocr.lineWorkers` | 自动 | 页内 CLS/REC 并行 |
| `MINIOCR_DET_THREADS` | `ocr.detThreads` | 自动 | 检测图内卷积线程 |
| `MINIOCR_USE_CLS` | `ocr.useCls` | **false** | 是否启用方向分类 |
| `MINIOCR_RASTER_WORKERS` | `ocr.rasterWorkers` | 自动（llm：`min(8,cores)`） | 并行 PDF 栅格生产者（封顶 8） |
| `MINIOCR_REC_BATCH` | — | **8** | `RecBatchLines` |
| `MINIOCR_DET_LIMIT_SIDE` | — | **960** | 检测 `LimitSideLength` |

调试 `/ocr` 可通过查询参数或遗留字段覆盖 DPI：`POST /ocr?dpi=150`，或 body 内 `"dpi":150`（竞赛字段优先；也可用遗留 `{"url":"...","dpi":150}`）。

#### CPU 自动扩缩（`autoScaleFromCpu`，默认 true）

旧公式 `engines = Clamp(cores/2, 4, 8)` 会在 **2 核机器上仍开 4 个引擎**。新曲线：

| 项 | 新公式 |
| --- | --- |
| `engines` | `Clamp(cores/2, 1, min(16, cores))` |
| `lineWorkers` / `detThreads` | 使 `engines × (line + det)` 约在 **1.0–1.5× cores**（目标约 1.25×） |
| `rasterWorkers` | `Clamp(min(engines, cores/2), 1, 8)` |

启动时日志打印 `ProcessorCount` 与选定的 engines/line/det/raster；`GET /health` 同样暴露这些字段及绝对 `configPath` / `configFileExisted` / LLM 状态（`llmApiKey` 仅为 `(set)`/`(empty)`）。

> 更快可降 `MINIOCR_DPI=45`；更高精度可设 `MINIOCR_DPI=150`、`MINIOCR_USE_CLS=1`。显式设置 env/文件中的 engines 等会关闭对该项的自动推算。

### 调试同步 `POST /ocr`（形状对齐竞赛回调）

本地调试时 `/ocr` **同步等待**并在响应体返回结果；请求/响应 JSON 尽量与竞赛协议一致，映射复用 `ChallengeResultMapper`（与异步回调同一套模型，无分叉）。

**请求**（竞赛兼容；`callbackUrl` 可省略/空，sync 不回调）：

```json
{
  "teamId": 0,
  "key": "debug",
  "callbackUrl": "",
  "files": [{ "fileId": "f1", "url": "http://127.0.0.1:8000/sample-multipage.pdf" }]
}
```

至少也可只传 `{"files":[{"fileId":"f1","url":"..."}]}`。遗留单文件 `{"url":"...","dpi":96}` 仍支持，会映射为 `files:[{fileId:"f1",url}]`。DPI 也可 `?dpi=` / `MINIOCR_DPI` / 配置文件。

**响应**（与回调 payload 同形）：

```json
{
  "teamId": 0,
  "key": "debug",
  "result": [
    {
      "fileId": "f1",
      "pages": [
        {
          "page": 1,
          "ruleList": [
            {
              "ruleCode": "B04",
              "ruleName": "人员名称",
              "ruleItemList": [{ "personName": "…", "count": 1, "originText": ["…"] }]
            },
            {
              "ruleCode": "B06",
              "ruleName": "公司名称",
              "ruleItemList": [{ "companyName": "…", "count": 1, "originText": ["…"] }]
            }
          ]
        }
      ]
    }
  ]
}
```

`/challenge` 仍为：快速 200 + 异步回调（见上文）。

### 示例 curl

```bash
# 终端 A：提供示例 PDF
python3 -m http.server 8000 --directory samples

# 终端 B：调试同步 OCR（竞赛形状）
curl -sS -X POST http://127.0.0.1:5080/ocr   -H 'Content-Type: application/json'   -d '{"teamId":0,"key":"debug","files":[{"fileId":"f1","url":"http://127.0.0.1:8000/sample-multipage.pdf"}]}' | jq .

# 遗留单 URL（仍可用）
curl -sS -X POST 'http://127.0.0.1:5080/ocr?dpi=96'   -H 'Content-Type: application/json'   -d '{"url":"http://127.0.0.1:8000/sample-multipage.pdf"}' | jq .

# 本地路径（Windows，中文文件名）。verbose=1 返回每页 text 与 ocrMs
curl.exe -sS -X POST "http://127.0.0.1:5080/ocr?verbose=1" ^
  -H "Content-Type: application/json; charset=utf-8" ^
  --data-binary "@body.json"

# 或上传文件
curl.exe -sS -X POST "http://127.0.0.1:5080/ocr/upload?verbose=1" ^
  -F "file=@C:\Users\mafuz\OneDrive\Desktop\你的文件.pdf;type=application/pdf"
```

`body.json` 用 UTF-8（无 BOM）保存，例如 `{"path":"C:\\Users\\mafuz\\OneDrive\\Desktop\\你的文件.pdf","dpi":96}`。PowerShell 5 的 `ConvertTo-Json` 默认编码容易把中文写坏，请用：

```powershell
$pdf = "C:\Users\mafuz\OneDrive\Desktop\你的文件.pdf"
$json = '{"path":"' + ($pdf.Replace('\','\\')) + '","dpi":96}'
[System.IO.File]::WriteAllText("$env:TEMP\ocr-body.json", $json, [System.Text.UTF8Encoding]::new($false))
curl.exe -sS -X POST "http://127.0.0.1:5080/ocr?verbose=1" -H "Content-Type: application/json; charset=utf-8" --data-binary "@$env:TEMP\ocr-body.json"
```

健康检查：

```bash
curl -sS http://127.0.0.1:5080/health
```

### 实体抽取（LLM 优先；无 LLM 后启发式可选）

`local` 模式在 OCR 进行中就开始抽取，不必等全书结束：

1. **LLM（推荐，`local` 模式文本 NER）**：若 `llm.enabled` 且配置了 `apiKey`，按 **非空页** 分组。默认每 **10** 个有文字的页组成一个请求（`llm.pagesPerRequest`，范围 1–2000），空白页（OCR 文本为空或只有空白）**不发给 LLM**，也**不出现在协议输出的 `pages` 里**。分组按文档顺序累计非空页，而不是固定的「第 1–10 页 / 11–20 页」窗口——这样空白页不会占掉名额，也不会产生整组为空的请求。页码仍写在提示词的 `--- page N ---` 里，N 是 PDF 原页码。某一组若再加一页就会超过 `maxCharsPerRequest`（默认 **300000**），则提前拆开；单页超限则截断后单独发送。请求在凑满一组时就发出（与后续 OCR 重叠），在途请求数不超过 `maxConcurrency`（默认 **8**）。提示词要求只返回严格 JSON `{"companies":["..."],"persons":["..."]}`，并写明什么算人名、什么算公司、如何把 OCR 拆开的汉字接回去。各批结果合并后做后处理：全角 ASCII 折成半角、去掉汉字之间的空白、丢掉对不上原文的幻觉、丢掉「张某」这类脱敏名和法院/政府机关、简称并进法定全称（分公司/分行仍单独保留），再回扫各非空页填充 `count` / `originText`。名字在页边界被拆开时，会看下一非空页开头 240 字。
2. **LLM 已调用后**：失败或结果为空时 **不**再回退 `EntityExtractor` 启发式——记错误日志并返回空 `entities`（避免静默启发式人名/公司名污染竞赛结果）。
3. **仅当 LLM 未启用 / 无 key**：若显式 `fallbackToHeuristics: true`，才使用 `EntityExtractor`（Regex + 百家姓 HashSet，`[GeneratedRegex]`，无 ML 包，AOT 安全）；默认 **false** → 空实体。空白页同样不进入协议输出。
4. **`ocr.mode=llm`（视觉）**：实体来自页级结构化 `ruleList`（及兼容的 companies/persons 字段），**不用**启发式 invent。视觉调用仍是一页一次（送出前无法知道该页有没有字）。返回文本为空且没有 `ruleList` 的页会从输出中去掉。

| 类型 | 启发式规则（摘要；仅 `fallbackToHeuristics: true` 且未走 LLM 时） |
| --- | --- |
| 公司名 | 中文组织后缀；英文 Inc/Ltd/Corp/LLC/Co.；标签 `公司名称：` / `甲方：` 等 |
| 人名 | 百家姓 + 职称/标签；英文 `John Smith` 式 |

冒烟：`dotnet run -c Release --project tests/MiniOcr.EntitySmoke`（启发式单元）。无 API key 时服务仍可启动；默认不会静默填启发式实体。

**长上下文提示：** 分组大小首先看 `pagesPerRequest`。`maxCharsPerRequest` 只是安全阀；DeepSeek Flash 等约 1M context 时一般不用把默认 300000 再拉高。仍需对照提供商 **token** 限额（中文约 1 字 ≈ 1–2 tokens）。

**局限：** 启发式会漏/误；LLM 依赖模型与 OCR 文本质量。后处理只保留正文里对得上的字，不会把模型没返回的名字补出来。竞赛场景请复核关键实体。

### 人名 / 公司名评测

离线集在 `tests/MiniOcr.EntityEval/dataset/samples.json`（合同、判决、跨行/跨页公司名、带空格的 OCR）。打分是**文档级名字集合**的精确率、召回率和 F1，人和公司分开，再给一个 micro。`count` / `originText` 另做形状检查：摘录 10–100 字且包含报出的名字。

**没有 API key 时**（CI 走这条）用 `recorded/responses.json` 里写好的模拟回复，只衡量后处理，不衡量提示词。模拟回复故意带了空格、称呼、简称、法院、脱敏名，并漏了一个人名。

```bash
dotnet run -c Release --project tests/MiniOcr.EntityEval -- --recorded
```

**用自己的 key 打真实端点**（OpenAI 兼容 Chat Completions，DeepSeek 把 `baseUrl` 设为 `https://api.deepseek.com`）：

```bash
export MINIOCR_LLM_BASE_URL=https://api.deepseek.com
export MINIOCR_LLM_API_KEY=sk-...
export MINIOCR_LLM_MODEL=deepseek-chat
export MINIOCR_LLM_THINKING=false   # 可选，默认关
dotnet run -c Release --project tests/MiniOcr.EntityEval -- --live
```

`--live` 会把同一套系统提示词和 `--- page N ---` 正文发给 `{baseUrl}/v1/chat/completions`，再走和生产一样的后处理。加 `--save-recorded out.json` 可以把原始回复存下来。未设置 `MINIOCR_LLM_API_KEY` 时 `--live` 不会发请求。

本仓库这次提交时环境里没有 key，**活模型的 P/R/F1 没有测**。`--recorded` 在同一金标上的对照（精确字符串匹配）是：

| 路径 | 人员 P / R / F1 | 公司 P / R / F1 | micro F1 |
| --- | --- | --- | --- |
| 旧行为：只做空白折叠，名字必须是原文的精确子串，不过滤 | 0.435 / 0.667 / 0.526 | 0.455 / 0.625 / 0.526 | 0.526 |
| 现后处理 | 1.000 / 0.933 / 0.966 | 1.000 / 1.000 / 1.000 | 0.984 |

人员召回不是 1，是因为模拟回复漏了「陈晨」，后处理不会编造这个名字。

**打分假设**（协议只规定了字段和 `originText` 长度，没有写全称/简称怎么算对）：

- B04 是自然人姓名。不要角色或职务本身、代称、含「某」的脱敏名。称呼（先生、经理）去掉。只出现在公司名内部的片段（「李宁体育用品有限公司」里的「李宁」）不算人。
- B06 是商事主体。保留以公司、集团、银行、信用社、事务所、合伙企业、合作社、厂结尾的名称，以及 Inc. / Ltd. / LLC / Corp. / Co. / Company / Corporation。律师事务所算公司。法院、检察院、政府、公安、管理局、仲裁委员会，以及大学、学院、医院、学校、研究院、研究所（名称里另有「公司」的除外）不算公司。
- 简称和法定全称同时被抽出时，只留更长的全称。母公司与它的分公司、支公司、分行、支行、营业部、办事处是两个主体，都留。
- 全角 ASCII（含括号）折成半角；汉字之间的空格和换行去掉。报出的名字必须能按这个规则对齐回 OCR，对不上就丢。`originText` 用同一套修复后的摘录，因此摘录里包含报出的名字。
- 模型只返回了「上海浦东发展银行」，而正文紧跟着「股份有限公司」时，会把法定结尾接上。紧跟着的「分公司」不会被接进母公司。

## SIMD 指令集档位（Native AOT，x64）

ILC 在**编译期**把指令集烧进二进制（Native AOT 不做运行时派发），所以 `Sdcb.SimdPaddleOCR` 里的 AVX2 / AVX-512 / VNNI 内核**只有编译时列进档位才存在**，否则会被当作死代码裁掉。默认档位 `avx2`：

```xml
<PublishAot>true</PublishAot>
<!-- 仅当 RuntimeIdentifier 含 x64 时启用；ARM64 不设置（基线已含 NEON） -->
<IlcInstructionSet Condition="'$(IlcInstructionSet)' == '' and $([System.String]::Copy('$(RuntimeIdentifier)').Contains('x64'))">avx2</IlcInstructionSet>
```

> **别去掉这行。** 一个档位都不设时 ILC 按 SSE2 / 128-bit `Vector<T>` 基线编译，`Avx2.IsSupported`、`Avx512F.IsSupported` 都会被折成 `false`（本机实测确认），AVX2 / AVX-512 内核整段裁掉，OCR 明显变慢。

用 `-p:IlcInstructionSet=<档位>` 覆盖（命令行属性优先级最高，会盖过 csproj 默认）：

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:IlcInstructionSet=avx512v2 -o ./artifacts/linux-x64-avx512v2
```

### CI 产出的档位（x64 两档）

| 产物后缀 | `IlcInstructionSet` | 目标 CPU | 参考延迟（本机实测） |
| --- | --- | --- | --- |
| （无） | `avx2` | Haswell（2013）及以后 | 382.7 ms/页 |
| `-avx512v2` | `avx512v2` | Ice Lake / Tiger Lake / Zen 4-5 / Sapphire Rapids 量级 | **246.8 ms/页（快约 35%）** |

基准口径：`samples/sample-multipage.pdf` 第 1 页 @ 96 DPI 灰度 + AA=None（793×1122），ChineseV6Tiny、单引擎、`LineWorkerCount=2` / `DetIntraOpThreads=1` / CLS off；warmup 8 次后取 20 次 p50。机器 i7-1165G7（4C/8T, Tiger Lake），绝对值为该机数值，**只看相对差**。

同口径其它档位（本机实测，未出 CI 包）：

| 档位 | 延迟/页 | 备注 |
| --- | --- | --- |
| `avx512` | 289.3 ms | -24%，但比 `avx512v2` 慢且并不更通用 |
| `avx512v3` | 245.2 ms | 与 `avx512v2` 持平，但要求更新的 CPU，不划算 |
| `avxvnni` | 拒绝启动 | 本机缺少该指令集 → 说明 VNNI 档位不可做默认 |
| `native` | 卡死 | 构建机自动探测；**不建议**（产物绑死构建机，且实测有风险） |
| 非 AOT（JIT） | 432.7 ms | 运行时自动探测，但整体比 AOT 慢 13% |

档位名就是 ILC `--instruction-set` 的名字，需要别的档位直接用同一行覆盖构建：

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:IlcInstructionSet=avx512v3 -o ./artifacts/linux-x64-avx512v3
```

（`x86-64-v3` 在本库上实测点亮的指令集合与 `avx2` 一致；`avx10v1` / `avx10v2` 对应 Granite Rapids 及更晚的硬件，目前没必要出包。）

### 档位不匹配不会崩，会明确报错

AOT 产物**启动时自带 CPU 能力检查**。档位高于 CPU 时不会执行非法指令，而是打印并退出：

```
The current CPU is missing one or more of the required instruction sets.
```

所以多档位并存是安全的，试错成本只是一次启动失败。

- **ARM64**（`linux-arm64` / `osx-arm64`）：不设置 `IlcInstructionSet`（基线已含 NEON）。本库的 ARM 内核只有 `AdvSimd`、没有 SVE，故未出额外 ARM 档位；需要时可用同样的 `-p:IlcInstructionSet=armv8.2-a` 自建。macOS **仅发布 Apple Silicon（`osx-arm64`）**，不再构建 Intel `osx-x64`。
- **不是所有机器都能跑最高档**：`avx512v2` 在较老的 x64（无 AVX-512）上会在启动时报错退出，此时用无后缀的 `avx2` 包。
- **非 AOT 单文件包不做档位**：JIT 在运行时探测 CPU，AVX2 / AVX-512 / VNNI 都会自动亮（本机实测 `Avx512F.IsSupported = true`），代价是整体比 AOT 慢。
- AOT 禁用反射密集 API；本项目使用 `JsonSerializerContext` + `WebApplication.CreateSlimBuilder`。

## 下载 CI 产物（GitHub Actions）

推送到 `main`、手动 `workflow_dispatch`，或发布 Release / 打 `v*` 标签时，工作流 [`.github/workflows/publish.yml`](.github/workflows/publish.yml) 会为各 RID 构建 Native AOT 并上传制品。

1. 打开仓库 **Actions** → 选中 **Publish Native AOT** 某次成功运行。
2. 在 **Artifacts** 下载对应平台 zip。x64 每个 SIMD 档位各一个，挑匹配 CPU 的档位（档位含义见 [SIMD 指令集档位](#simd-指令集档位native-aotx64)）：
   - `miniocr-win-x64` / `miniocr-linux-x64`（AOT，**`avx2` 档，最通用**，竞赛默认）
   - `miniocr-win-x64-avx512v2` / `miniocr-linux-x64-avx512v2`（AOT，**最高档，实测快约 35%**，需 CPU 支持 AVX-512）
   - `miniocr-osx-arm64`（**Apple Silicon only**）/ `miniocr-linux-arm64`（AOT，NEON 基线）
   - `miniocr-win-x64-singlefile` / `miniocr-linux-x64-singlefile`（**真正单文件**，非 AOT，运行时自动探测 SIMD）
   - 不再提供 `osx-x64` / Intel Mac 包
3. 若通过 **Release** / `v*` 标签触发，zip 也会尽量挂到该 GitHub Release 上，可直接从 Releases 页下载。

**AOT zip：** 解压后含可执行文件 + 原生依赖（`libSkiaSharp` / `pdfium` 的 `.dll` / `.so` / `.dylib`），以及示例 PDF（若打包时存在）；x64 包要求 CPU 满足所选的 SIMD 档位，不满足会在启动时报错退出。  
**单文件 zip：** 解压后通常只有一个 `MiniOcr`（或 `MiniOcr.exe`），拷走即可运行。

## 架构与内存策略

| 环节 | 策略 |
| --- | --- |
| 下载 | `HttpClient`：若 `Accept-Ranges: bytes` 且已知 `Content-Length`，则并行 Range 写入预分配缓冲；否则单流写入预分配/可控增长缓冲。硬顶 **300 MB**。缓冲来自 `ArrayPool<byte>`。 |
| 栅格化 | PDFtoImage（PDFium + SkiaSharp）；**local** 默认 **96 DPI**，**llm** 未显式配置时默认 **72 DPI**；每 worker **一次** `PdfDocument.Load` + `ToImages`；`AntiAliasing=None` + `Grayscale`；多生产者写入有界 Channel，**绝不**同时持有全部页位图。llm 默认更多 raster workers（`min(8,cores)`）。 |
| OCR | **local**：复用多个 `PaddleOcrAll`（ChineseV6Tiny，默认可关 CLS）；页级引擎池互斥租用；Channel 上 raster↔OCR 重叠。**llm**：不加载 Paddle；**每页** JPEG（质量默认 70）经有界队列立刻交给视觉 worker（`ocrConcurrency`），与栅格重叠——不再等全本编码完才发第一张；优先直接产出 B04/B06 `ruleList`。 |
| 实体 | 优先 `LlmEntityExtractor`：每 10 个非空页一组 JSON NER，OCR 未结束即可发出，`maxConcurrency` 并行；空白页不发送、不出现在输出。集群默认把这些组发给有 LLM key 的节点（`cluster.distributedNer`），协调节点合并去重。回来后按原文对齐、过滤非公司/脱敏名并合并简称。失败/关闭则 `EntityExtractor` 启发式。 |
| JSON | 源生成 `AppJsonContext`，AOT 友好。 |

### 峰值内存（量级，非承诺值）

- PDF 本体：最多约 **300 MB**（池化租用）
- 在途页位图：窗口内数页（DPI 越低越小）
- 模型 + 推理工作区：随引擎数上升；关闭 CLS 可明显降低
- 设计目标：2000 页时内存不随页数线性涨到「整本位图」，而随 **窗口 + 模型** 近似封顶

## 依赖

| 包 | 说明 |
| --- | --- |
| `external/SimdPaddleOCR` @ `6aae0ad` | [fork](https://github.com/huiyuanai709/SimdPaddleOCR) `main` 的 `ProjectReference`（`.gitmodules` 里 `branch = main`），不再使用 NuGet `Sdcb.SimdPaddleOCR` 1.4.2。Apache-2.0 |
| 同子模块内 `ChineseV6Tiny` | 中文 tiny DET+REC（CLS 可选），与引擎同一棵源码树，避免和 NuGet 模型包的类型不一致 |
| `PDFtoImage` 5.4.0 | PDFium 栅格化（SkiaSharp） |

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `POST` | `/challenge` | **竞赛 serviceUrl（推荐）**：异步受理，见上文「竞赛协议」 |
| `POST` | `/` | 与 `/challenge` 相同（可将 serviceUrl 填根路径） |
| `GET` | `/health` | 健康、模型与当前旋钮。`cluster.enabled` 时附带节点、容量、各节点已完成页数 |
| `POST` | `/ocr` | 调试用同步 OCR：竞赛兼容 `files[{fileId,url}]`，响应同回调 `result` 形状（遗留 `{url,dpi?}` 仍可用） |
| `GET` | `/` | 纯文本接口说明 |
| `GET` | `/cluster/info` | 集群探活（需 token）：本节点 mode / model / dpi / capacity |
| `POST` | `/cluster/register` `/cluster/heartbeat` `/cluster/dispatch` | 工人向协调节点注册、心跳、拉取任务（需 token） |
| `GET` | `/cluster/jobs/{id}/pdf` | 工人下载该任务的 PDF 一次（需 token） |
| `POST` | `/cluster/jobs/{id}/join` `/claim` `/result` `/fail` | 领取页批次、回传文本、失败重排队（需 token） |
| `POST` | `/cluster/notify` | 协调节点通知工人有新任务（需 token；工人也可以只靠轮询） |

`/challenge` 与 `/ocr` **不加**集群 token（竞赛平台无登录）。只有 `/cluster/*` 校验共享密钥。

## 分布式 OCR（cluster）

本地 OCR 是 CPU 活。协调节点把**页**分给多台机器（含它自己）。大文档上墙钟应随总核数下降；很短的文档会被领页和打开 PDF 的固定开销吃掉，见下面的实测。未配置 `cluster` 或 `enabled: false` 时，走原来的单机流水线，协议 JSON 不变。

### 为什么传 PDF + 页码，而不是页图

每个工人**下载一次 PDF**，再按页码批次自己栅格化。回传的只是每页文本（KB 级）。

页图和 PDF 谁更大取决于文档。在开发机上用 PyMuPDF（栅格器和 PDFium 不同，只作数量级）把 `samples/sample-multipage.pdf`（558351 字节，5 页）按 96 DPI 灰度栅格：5 页 PNG 合计 290755 字节、JPEG q70 合计 199416 字节，分别是 PDF 的 0.52 和 0.36。第 1 页 794×1123，PNG 58207 字节、JPEG 40266 字节。这份样例上，压缩页图比整本 PDF 更小。

仍然每个工人传一次 PDF、不传页图：

- 协调节点若先替别人栅格并编码 PNG/JPEG，花的是本来要拿去 OCR 的 CPU，而且编码卡在每个批次的关键路径上。PDF 只下一次，和协调节点自己已经在跑的本地 OCR 重叠。
- 工人拿到 PDF 后走现有的 `ToImages`（文档复用），栅格 CPU 留在即将 OCR 的机器上。
- 竞赛目标大约 300 MB / 2000 页。按上面这种文本页 JPEG ~40 KB 估算，2000 页大约 80 MB；扫描页更大，约 150 KB/页时整本图和一份 300 MB 的 PDF 同级。无论谁更大，页图都要按批次反复上传；PDF 是每个工人一次传输。
- 带宽因此是「工人数 × PDF 大小」一次，加上回传的小 JSON。

工人用自己配置的 OCR 模式（默认还是 `local` / ChineseV6Tiny）。Windows 工人可以配 `wechat`，领到的页走本机微信插件；协调节点把这种模型记成 `wechat-<kind>`。任务 DPI 以协调节点本次请求为准，各节点按这个 DPI 渲染。若某节点的 **mode、模型名或配置 DPI** 与协调节点不一致，协调节点打 **warning**（文本可能对不齐），任务仍会继续。

### 怎么分活

拉模式，不是按页数切死：

1. 协调节点创建任务，自己的引擎池是节点之一，立刻开始领页。
2. 工人 `POST /cluster/dispatch` 轮询（或被 `POST /cluster/notify` 叫醒）后 `GET` PDF 一次，再 `POST /claim` 领下一批。
3. 批次大小约等于该节点 `capacity`（默认本机引擎数；`llm` 模式用视觉并发），封顶 16，临近结尾缩到 1–2 页，避免尾巴粘在一台慢机器上。
4. 一台机器同时在途的页数不超过它的 capacity，所以更快的机器更早来领下一批，自然多干。
5. 短文档上协调节点会在 `joinGraceMs`（默认 500）内先把自己限制在一个窗口，给工人留出下载时间；大文档上这点时间可以忽略。过了宽限，本地节点继续把剩下的页吃完。
6. 租约到期、`/fail`、或健康检查连续失败：这些页回到队列，别的节点（含本地）重做。投机执行：待处理队列空了且未完成页数 ≤ `speculativeTailPages`（默认 4）时，空闲节点会再跑一遍尾巴，谁先写回谁算数。
7. 到达 `jobDeadlineSeconds`（默认 300）仍有远程租约：协调节点丢弃远程租约，剩下的页只在本地做完。死掉的工人不会让任务挂死或直接失败。
8. 页按完成顺序写入，但 LLM NER 仍用原来的有序缓冲：凑满**连续的** 10 个非空页就发出一组，空白页不占名额、也不进协议输出。最终每页文本与单机相同（同一模型、同一 DPI）。

`GET /health` 在集群开启时多一个 `cluster` 对象：节点、容量、健康、在途页、累计完成页。任务进行中大约每 3 秒或每 10% 有一行进度（`done/total`、pages/s、各节点页数），结束时另有一行 `byNode=coord=.., worker-a=..`，以及 `OCR_TEXT_SHA256=`（全页文本哈希，含空白页，便于和单机对照）。这些都不进竞赛 JSON。调度日志见下面「日志」。

### 这台开发机上的计时

4 核 Linux。每个进程 `taskset` 绑 1 核、引擎数 1、96 DPI、每批 1 页。20 页是把样例 PDF 重复 4 次（561183 字节）。哈希是全部页文本（含空白页）的 SHA-256，和单机逐页文本一致。

| 跑法 | 墙钟 | 提交页数 | 文本哈希 |
| --- | --- | --- | --- |
| 单机，5 页 | 2233 ms | — | `ebff69493a49e0ed8b01d52587c71ef3295abd8e326e718771f11c511f77e6d0` |
| 单机，20 页 | 5203 ms | — | `0f89d18fbf7b5cc23f62d6348cbaedbec505e10c84f3339a250092e32d008f6a` |
| 三节点，5 页 | 2190 ms | coord 3 / worker-a 1 / worker-b 1 | 与单机相同 |
| 三节点，20 页，`joinGraceMs=20000`（`ClusterLive` 把协调节点按住） | 3960 ms | coord 1 / worker-a 10 / worker-b 9 | 与单机相同 |
| 同上，中途杀掉 worker-b | 4851 ms | coord 1 / worker-a 19（worker-b 提交 0，页被重做） | 与单机相同 |

另一次把 `joinGraceMs` 改成默认 500：20 页 4647 ms，提交页 coord 9 / worker-a 5 / worker-b 6，哈希仍是上面的 20 页值（那次单机 20 页是 5333 ms）。

20 页、每批 1 页时，领页 HTTP 和每台打开 PDF 的固定开销还压得过 OCR，所以 3 个单核节点大约 1.15×（默认宽限）到 1.31×（协调节点让出），不是 3×。页数到几百、两千时，OCR 会盖过这些开销。`tests/MiniOcr.ClusterLive` 用 20 秒宽限，是为了让 5 页样例也能摊到工人上；默认 500 ms 适合大文档。

### 配置

同一份 `config.json` 的 `cluster` 段。环境变量 `MINIOCR_CLUSTER_*` 覆盖文件。`enabled` 但 `token` 为空时会记一条警告并**保持关闭**。

协调节点（Windows 笔记本示例，监听所有网卡）：

```json
{
  "ocr": { "mode": "local", "dpi": 96, "autoScaleFromCpu": true },
  "cluster": {
    "enabled": true,
    "role": "coordinator",
    "nodeId": "laptop",
    "advertiseUrl": "http://192.168.1.20:5080",
    "token": "replace-with-a-long-random-secret",
    "capacity": 0,
    "joinGraceMs": 500,
    "leaseSeconds": 20,
    "pageTimeoutSeconds": 20,
    "healthIntervalSeconds": 5,
    "jobDeadlineSeconds": 300,
    "speculativeTailPages": 4,
    "verboseDispatch": false,
    "distributedNer": true,
    "workers": [
      { "url": "http://192.168.1.30:5081", "capacity": 0 }
    ]
  }
}
```

工人节点（Mac）可以不写进上面的 `workers`，自己来注册：

```json
{
  "ocr": { "mode": "local", "dpi": 96, "autoScaleFromCpu": true },
  "cluster": {
    "enabled": true,
    "role": "worker",
    "nodeId": "mac",
    "advertiseUrl": "http://192.168.1.30:5081",
    "coordinatorUrl": "http://192.168.1.20:5080",
    "token": "replace-with-a-long-random-secret",
    "capacity": 0
  }
}
```

`capacity: 0` 表示用本机引擎数。`workers[].capacity: 0` 表示等对方 `/cluster/info` 或注册报文里的容量。

### 分布式文本 NER

OCR 仍按页批租约拉。文本 NER（`local` 模式，不是 `ocr.mode=llm` 的视觉抽取）在 `cluster.distributedNer` 为开时（**默认开**）不再堆在协调节点上：

1. 协调节点按和单机相同的规则切 NER 组：连续非空页、每组最多 `llm.pagesPerRequest`（默认 10）、超过 `maxCharsPerRequest` 提前拆开、空白页不进组。
2. 一组在**下一非空页的前 240 字**已经知道（或全书 OCR 结束）之后才可被领取。领取报文带上这 240 字。工人把它附在提示词末尾，只用于接上被页边界拆开的名字；协调节点最后仍用**全书原文**做对齐，所以组落在不同节点上时，跨节点的拆名不会丢。
3. 有 LLM key 的节点用**自己的** `llm.apiKey` / `llm.maxConcurrency` 调 Chat Completions，回传公司名、人名，以及带页码、`count`、`originText` 的实体。协调节点把各组合并、去重，再跑和单机一样的 `EntityPostProcessor`，回调格式不变。
4. 没配 key 的工人只做 OCR。NER 组由其它有 key 的节点（含协调节点）领取。
5. 工人 LLM 失败、租约到期或节点被判不健康时，该组重新排队，别的节点或协调节点重做。同一组最多尝试 8 次，避免一条坏请求把任务挂死。协调节点自己没有 key、到了 `jobDeadlineSeconds` 仍无人能跑时，放弃剩余组并记警告，已完成的 OCR 页仍在。
6. `GET /health` 的 `cluster.nodes[]` 带 `llmConfigured` 和 `nerConcurrency`。`cluster.distributedNer` 也在 `cluster` 对象上。

进度汇总在原来的 OCR `byNode=` 之外加上 `ner=已完成/已成组 nerInFlight=… nerByNode=…`。每组领取和完成默认是 Debug，`verboseDispatch` 时升到 Information。

**速度（估算，不是端到端实测）：** 2000 页、几乎无空白时大约 200 个 NER 组。单协调节点 `maxConcurrency=8` 约 25 波 LLM 调用；3 台各自 concurrency 8（合计 24）约 9 波，LLM 阶段大约 **2.8×**。前提是每台用自己的 key、提供商吃得下合计并发，并且 OCR 已经把组喂出来。共享一个 key 时提供商限流会把加速吃掉。视觉模式（`ocr.mode=llm`）的实体来自页级 `ruleList`，不走这条分布式文本 NER。

两边 DPI、`ocr.mode`、模型保持一致（都用默认 `local` + ChineseV6Tiny）。Mac 用 `osx-arm64` 包，Windows 用 `win-x64`（或 `win-x64-avx512v2`）包，Linux 工人用对应 RID。协议是 HTTP + JSON，不共享进程或原生库。

启动：

```bash
# Windows（协调节点）
MiniOcr.exe --urls http://0.0.0.0:5080

# macOS（工人）
./MiniOcr --urls http://0.0.0.0:5081
```

环境变量（覆盖文件，便于一台机器临时改角色）：

| 变量 | 含义 |
| --- | --- |
| `MINIOCR_CLUSTER_ENABLED` | `1` / `true` 打开 |
| `MINIOCR_CLUSTER_ROLE` | `coordinator`、`worker` 或 `both` |
| `MINIOCR_CLUSTER_TOKEN` | 共享密钥 |
| `MINIOCR_CLUSTER_NODE_ID` | 节点名 |
| `MINIOCR_CLUSTER_ADVERTISE_URL` | 别的机器访问本进程的基址 |
| `MINIOCR_CLUSTER_COORDINATOR_URL` | 工人要连的协调节点 |
| `MINIOCR_CLUSTER_WORKERS` | 逗号分隔的工人 URL（仅协调节点） |
| `MINIOCR_CLUSTER_CAPACITY` | 覆盖本机容量 |
| `MINIOCR_CLUSTER_PAGES_PER_BATCH` | 固定批次大小（默认按容量自动） |
| `MINIOCR_CLUSTER_LEASE_SECONDS` | 批次租约下限 |
| `MINIOCR_CLUSTER_PAGE_TIMEOUT_SECONDS` | 每页额外租约 |
| `MINIOCR_CLUSTER_HEALTH_INTERVAL_SECONDS` | 探活 / 心跳间隔 |
| `MINIOCR_CLUSTER_JOB_DEADLINE_SECONDS` | 到点后本地接管剩余页 |
| `MINIOCR_CLUSTER_JOIN_GRACE_MS` | 开局留给工人下载 PDF 的本地窗口 |
| `MINIOCR_CLUSTER_SPECULATIVE_TAIL` | 尾巴投机复制的页数上限 |
| `MINIOCR_CLUSTER_VERBOSE_DISPATCH` | `1` / `true` 时把领页、心跳、批次完成、空轮询打到 Information。默认关闭（这些行在 Debug） |
| `MINIOCR_CLUSTER_DISTRIBUTED_NER` | `cluster.distributedNer`。默认 **开**（集群启用时）。`0` / `off` 时文本 NER 仍全部在协调节点上 |

### 日志

多机时领页很密。协调节点每次 `claim`、工人每批完成，以前都打在 Information。再加上框架对每一次 HTTP（空转的 `POST /cluster/dispatch` 大约每秒 4 次、心跳、领页、回传）各打多行 Information，控制台就看不见 OCR / NER 了。

默认 Information 保留：

- 任务开始、PDF 下载、渲染 / OCR / NER 的阶段和耗时、任务结束
- 进度汇总：大约每 3 秒，或每跨过 10%（两次至少隔 1 秒）：`done/total`、pages/s、`byNode=`。分布式 NER 打开时同一行带 `ner=已完成/已成组 nerInFlight= nerByNode=`。结束时仍有一行带 pages/s 的 `byNode=`（以及 NER 汇总）
- 工人注册、加入任务、离开任务（离开行带本节点页数）
- 租约到期后页被重新排队、投机重试
- 全部 warning / error

改到 Debug 的例行事件（没有任务时的空轮询以前不打应用日志，现在也只在 Debug）：

- 每次领页 / 发放 OCR 租约，以及每次领取 / 完成 NER 组
- 心跳成功
- 每批完成、协调节点收下一批结果
- 空的 dispatch / claim 轮询

框架日志：未单独配置时，`Microsoft.AspNetCore`（入站 Request starting / Executing endpoint）和 `System.Net.Http.HttpClient`（出站每个请求四行）降到 Warning。`Microsoft.Hosting.Lifetime` 的监听地址仍是 Information。已经写了 `Logging:LogLevel` 的类别不会被覆盖。

要看回每批调度明细，二选一：

- `cluster.verboseDispatch: true`，或 `MINIOCR_CLUSTER_VERBOSE_DISPATCH=1`（上面的 Debug 行升到 Information，默认日志级别就能看见）
- 不改这个开关，把类别调到 Debug：`Logging__LogLevel__MiniOcr.Services.ClusterCoordinator=Debug` 和 `Logging__LogLevel__MiniOcr.Services.ClusterWorkerHost=Debug`

要看每条 HTTP：`Logging__LogLevel__Microsoft.AspNetCore=Information` 和 `Logging__LogLevel__System.Net.Http.HttpClient=Information`。

调度和竞赛 JSON 不变。

### 防火墙和端口

流量是双向的，但**工人主动连协调节点**就够跑起来：

- 协调节点入站：`advertiseUrl` 的 TCP 端口（注册、心跳、拉任务、下载 PDF、回传结果）。Windows 防火墙要放行这个端口；只对局域网开放，不要把没有 TLS 的端口暴露到公网（token 是共享密钥，不是用户体系）。
- 工人出站：访问协调节点即可。家用路由器后面的笔记本 / Mac 通常不用做端口映射。
- 协调节点到工人的 `POST /cluster/notify` 和 `GET /cluster/info` 是可选加速。工人入站被系统防火墙拦住时，通知会失败并记警告，工人改为轮询 `coordinatorUrl`，任务仍然完成。心跳新鲜时，探活失败**不会**把节点判死。
- 竞赛 `serviceUrl` 只打到协调节点的 `/challenge`。工人不需要公网地址。
- `advertiseUrl` 填局域网 IP，不要填 `127.0.0.1`（那只对同一台机器上的多进程测试有意义）。

`role: both` 表示本进程既接 `/challenge` 并分发，也接受别的协调节点派来的页。不要把 `coordinatorUrl` 指回自己。

调度与鉴权（不加载 OCR 模型）：

```bash
dotnet run -c Release --project tests/MiniOcr.ClusterSmoke
```

本机多进程（协调节点 + 两个工人，会真正跑 ChineseV6Tiny）：

```bash
dotnet run -c Release --project tests/MiniOcr.ClusterLive
```

## 项目结构

```
miniocr/
  MiniOcr.csproj          # Web + PublishAot + IlcInstructionSet=avx2（仅 x64）；可选 MiniOcrSingleFile
  .gitmodules             # external/SimdPaddleOCR tracks fork main (pinned 6aae0ad)
  external/SimdPaddleOCR/ # fork 源码（ProjectReference；CI checkout 带 submodules）
  .github/workflows/publish.yml  # 多平台 AOT + linux/win 单文件矩阵
  Program.cs              # SlimBuilder + /challenge /ocr /health
  AppJsonContext.cs       # AOT JSON
  Models/OcrModels.cs
  Services/
    AppConfigStore.cs     # config path resolution (AppData / Application Support / ~/.config)
    OcrRuntimeConfig.cs   # 文件+环境变量+CPU 自动扩缩
    ClusterRuntimeConfig.cs
    ClusterPageScheduler.cs  # 拉模式页队列、租约、尾巴投机
    ClusterNerScheduler.cs   # 分布式文本 NER 分组、240 字页头、失败重试
    ClusterCoordinator.cs
    ClusterWorkerHost.cs
    ClusterEndpoints.cs
    LlmEntityExtractor.cs # OpenAI 兼容 Chat Completions NER
    ParallelPdfDownloader.cs
    RentedBuffer.cs
    OcrEngine.cs
    WeChat/WeChatOcrEngine.cs  # 实验：Windows 微信插件（mmmojo）
    OcrCompareRunner.cs   # MiniOcr --compare
    PdfOcrPipeline.cs
    EntityExtractor.cs    # 启发式回退
    ChallengeJobService.cs # 竞赛异步队列 + 回调
    ChallengeResultMapper.cs # 按页 B04/B06 + originText
  tests/MiniOcr.EntitySmoke/   # 实体抽取冒烟
  tests/MiniOcr.WeChatSmoke/   # 微信 OCR 定位与 protobuf（不连微信）
  tests/MiniOcr.ClusterSmoke/  # 调度器 / 配置 / 鉴权（不加载模型）
  tests/MiniOcr.ClusterLive/   # 本机多进程：协调节点 + 2 工人
  samples/sample-multipage.pdf
  README.md
```

## 实测：2000 页（AOT，请勿伪造）

以下数字来自本机对 `samples/sample-2000.pdf`（2000 页，中英混合文本）的真实测量（Asia/Shanghai）：

### 端到端 OCR（优化前，Native AOT）

| 项 | 旧默认 DPI 45 | DPI 96（栅格优化前） |
| --- | ---: | ---: |
| 日期 | 2026-09-20 23:52 CST | **2026-09-21 07:10–07:18 CST** |
| 配置 | DPI 45 / 引擎 4 / Line 4 / Det 2 / CLS off | **DPI 96 / 引擎 4 / Line 4 / Det 2 / CLS off / raster 4** |
| 模型 | ChineseV6Tiny | **ChineseV6Tiny** |
| `timings.totalMs` | 248,854.5（~4.15 min） | **456,691.7（~7.61 min）** |
| pages/sec | 8.037 | **4.379** |
| 是否 &lt; 5 min | 是 | **否** |
| peak RSS | ~417 MiB | **~1459 MiB** |
| downloadMs | 5.9 | **4.4** |
| rasterizeMs（页合计） | 32,902 | **46,902.7** |
| ocrMs（页合计） | 994,282 | **1,823,128.4** |
| 页尺寸（宽×高） | 372 × 526 | **793 × 1122** |

### 栅格化加速（2026-09-21，DPI 96）

瓶颈在 **native PDFium**（逐页 `Conversion.ToImage` 会 **每页重新 Load** PDF）。托管 SIMD 帮不上忙（无像素拷贝；Skia 已是 BGRA）。PDFium/Skia 自身已用原生 SIMD。

| 场景 | 页数 | workers | wall | 页合计（≈`rasterizeMs`） | ms/页（合计） |
| --- | ---: | ---: | ---: | ---: | ---: |
| 优化前 `ToImage`/页 | 2000 | 4 | 27,323 ms | 108,057 ms | 54.0 |
| **优化后** `ToImages`+AA=None+Gray | 2000 | 4 | **2,751 ms** | **10,866 ms** | **5.43** |
| 同上 | 2000 | 2 | 2,611 ms | 5,176 ms | 2.59 |
| 流水线冒烟（非 AOT，含 OCR） | 200 | 4 | total 22.7 s | **rasterizeMs 882** | 4.4 |

约 **~10×** 栅格 wall / **~4–5×** 相对端到端里旧的 `rasterizeMs≈46.9 s`（流水线与 OCR 争用下页合计更接近 ~11 s）。OCR 仍占主导，端到端总时长几乎不变。

**未换引擎：** Docnet / 直连 Pdfium / MuPDF 探针无必要——文档复用 + 关闭 AA 已吃掉主要浪费；PDFtoImage 已 AOT 友好。

更早基线（DPI 150 / 引擎 2 / CLS on）：`totalMs` 838,569.1（~14.0 min）。

冒烟（5 页 `sample-multipage.pdf`）仍可用于快速验证；大吞吐请以 2000 页表为准。

**精度 / 速度权衡：** 默认 DPI 96 相对 150 像素面积约 41%，相对旧默认 45 约 4.6×；中文细部明显好于 45。栅格默认 `AntiAliasing=None` + `Grayscale`（仍 BGRA，利于 OCR 锐利字形）。若需 &lt;5 min / 2000 页可降 `MINIOCR_DPI=45`；更高精度可设 `MINIOCR_DPI=150` 并视情况开启 `MINIOCR_USE_CLS=1`。

## 许可证

示例代码以仓库为准；Sdcb.SimdPaddleOCR 与模型包遵循其上游 Apache-2.0 等许可。
