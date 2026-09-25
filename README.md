# MiniOCR

基于 [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) 的 **Native AOT** PDF OCR HTTP API（**.NET 11 RC / `net11.0`**）。

从 URL 并发下载 PDF（≤300 MB），按页流式栅格化 + OCR（最多约 2000 页），返回每页文本、耗时，以及 **公司名 / 人名** JSON。

**OCR 模式（`ocr.mode`）：**

- **`local`（默认）**：本地 PP-OCRv6 **ChineseV6Tiny**；实体抽取优先 OpenAI 兼容 LLM NER，可回退启发式。
- **`llm`**：跳过本地 Paddle 模型加载；将每页 JPEG 以 `image_url` data URL 发给多模态 Chat Completions，一次调用尽量直接返回竞赛形状的 B04/B06 `ruleList`（可配高并发，I/O 密集）。
- **`hunyuan`**：同样跳过 Paddle。把每页彩色 JPEG 发给**本机**已启动的 [HunyuanOCR](https://huggingface.co/tencent/HunyuanOCR) OpenAI 兼容服务（vLLM 或 llama.cpp `llama-server`），使用官方文档解析提示词。页文本出来后，B04/B06 仍走与 `local` 相同的 NER。不是进程内引擎，也**达不到** 2000 页 / 5 分钟（见下方专节）。

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
git clone https://github.com/huiyuanai709/miniocr.git
cd miniocr
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
    "autoScaleFromCpu": true
  },
  "hunyuan": {
    "enabled": true,
    "baseUrl": "http://127.0.0.1:8000",
    "apiKey": "",
    "model": "tencent/HunyuanOCR",
    "timeoutSeconds": 180,
    "concurrency": 2,
    "jpegQuality": 85,
    "maxTokens": 8000,
    "prompt": ""
  }
}
```

**优先级：**

- OCR：环境变量 `MINIOCR_*` **覆盖** 文件；文件中 `null` / 未写且 `autoScaleFromCpu: true` 时按 CPU 核数自动推算。`MINIOCR_OCR_MODE` 覆盖 `ocr.mode`（`local` / `llm` / `hunyuan`）。
- LLM：主要读配置文件；可用 `MINIOCR_LLM_API_KEY` / `MINIOCR_LLM_BASE_URL` / `MINIOCR_LLM_MODEL` / `MINIOCR_LLM_MAX_CONCURRENCY` / `MINIOCR_LLM_OCR_CONCURRENCY` / `MINIOCR_LLM_OCR_JPEG_QUALITY` / `MINIOCR_LLM_THINKING` 覆盖。也可用 `MINIOCR_CONFIG_PATH` 指定配置文件。**不会**把 `apiKey` 打进日志（仅显示 `(set)` / `(empty)`）。
- Hunyuan：`MINIOCR_HUNYUAN_BASE_URL` / `MINIOCR_HUNYUAN_API_KEY` / `MINIOCR_HUNYUAN_MODEL` / `MINIOCR_HUNYUAN_CONCURRENCY` / `MINIOCR_HUNYUAN_JPEG_QUALITY` / `MINIOCR_HUNYUAN_MAX_TOKENS` / `MINIOCR_HUNYUAN_PROMPT` / `MINIOCR_HUNYUAN_TIMEOUT` / `MINIOCR_HUNYUAN_ENABLED` 覆盖 `hunyuan` 段。同样不打印 apiKey。

#### OCR 模式：`local` / `llm` / `hunyuan`

| `ocr.mode` | 行为 | 何时用 |
| --- | --- | --- |
| `local`（默认） | 加载 ChineseV6Tiny / `PaddleOcrAll`；栅格后本地 OCR；可选 LLM **文本** NER | 离线、控成本、低延迟本机推理；竞赛时延主路径 |
| `llm` | **不加载**本地 Paddle 模型（更快启动、更省 RAM）；栅格→JPEG→多模态 Chat Completions（与视觉 **流水线重叠**） | 有视觉模型配额、希望直接出 B04/B06 |
| `hunyuan` | **不加载** Paddle；彩色栅格→JPEG→本机 HunyuanOCR 服务（官方文档解析提示词）→ 与 `local` 相同的文本 NER | 要 HunyuanOCR 的版面/正文质量，且另有 GPU 或能接受很慢的 CPU |

设置方式：

```json
"ocr": { "mode": "llm" }
```

或：`export MINIOCR_OCR_MODE=llm`。未在配置/`MINIOCR_DPI` 中显式设置 DPI 时，`llm` 默认 **72**，`hunyuan` 默认 **144**（`local` 仍为 **96**）；需要更高清晰度可设 `"dpi": 96` 或 `150`。示例 `config.json` 里写了 `"dpi": 96`，切换 mode 时这个显式值仍然优先。

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

#### HunyuanOCR（`ocr.mode=hunyuan`）

腾讯 [HunyuanOCR](https://huggingface.co/tencent/HunyuanOCR)（约 **1B** 参数，架构 HunYuanVL）是端到端 OCR 视觉语言模型，不是 Paddle 那种可以嵌进 Native AOT 的小检测/识别网络。

**进程内不可行（因此本模式是 HTTP 客户端，不是第二个 `OcrEngine`）：**

| 运行时 | 官方现状 | 能否放进本仓库的 Native AOT |
| --- | --- | --- |
| transformers ≥ 5.13（`HunYuanVLForConditionalGeneration`） | Python 推理 | 否 |
| vLLM OpenAI 服务（CUDA） | 官方服务端 | 否，独立进程 |
| llama.cpp GGUF + mmproj，`llama-server`（上游 ≥ b11103，含 DFlash） | 官方 CPU / 消费级 GPU 路径，同样是 OpenAI HTTP | 否。交付形态是 `llama-server`，没有可供 AOT P/Invoke 的稳定多模态绑定 |
| ONNX Runtime | 无官方导出 | 否 |

权重不进 git（语言 GGUF 的 fp16 就大约 2GB，再加上视觉 mmproj）。配置里只写服务地址。

**2000 页 / 5 分钟不现实。** 竞赛目标约 **6.7 页/秒**。官方数字来自上游 `docs/benchmark.md`（单卡 NVIDIA H20 80GB，并发 1，`max_tokens=8000`，OmnidocBench）和 `docs/llama_cpp.md` 的样例，不是本仓库这台机器实测：

| 配置 | 延迟 | 吞吐 | 2000 页外推（单流） |
| --- | ---: | ---: | ---: |
| H20，HunyuanOCR AR | 3.03 秒/页 | 0.330 页/秒 | **约 101 分钟** |
| H20，HunyuanOCR + DFlash | 1.41 秒/页 | 0.706 页/秒 | **约 47 分钟** |
| Apple M5 Pro Metal，文档样例 | 6.885 秒/页 | 0.145 页/秒 | **约 3.8 小时** |

即便乐观假设同一张 H20 上批处理能线性放大 8 倍（官方没给这个数，图像 prefill 通常做不到线性），DFlash 也只有约 5.6 页/秒，仍低于 6.7。CPU 更慢：1B 级模型常见大约每秒数个到几十个 token，官方 markdown 提示词一页经常是数百到数千 token。按 10 token/秒 × 800 token ≈ **80 秒/页**（2000 页约 **44 小时**）；按 30 token/秒 × 400 token ≈ **13 秒/页**（仍约 **7 小时**），还没算视觉编码器 prefill。对照本仓库 Paddle：DPI 96 约 4.4 页/秒（2000 页约 7.6 分钟），DPI 45 约 8 页/秒（约 4.2 分钟）。Hunyuan 是质量路径，不是时延路径。

**请求：** `POST {baseUrl}/v1/chat/completions`，`temperature=0`，`max_tokens` 默认 8000。用户消息先图后文，提示词默认官方文档解析句（`prompt` 为空时）：

```text
提取文档图片中正文的所有信息用markdown格式表示，其中页眉、页脚部分忽略，表格用html格式表达，文档中公式用latex格式表示，按照阅读顺序组织进行解析。
```

想少生成 token 可把 `prompt` 改成 `提取图中的文字。`。不要改成竞赛 `ruleList` JSON：上游说明换掉模型自己的提示词会伤准确率。返回的是页文本；B04/B06 再走 `LlmEntityExtractor`（要 `llm.enabled` + apiKey）或 `llm.fallbackToHeuristics=true`。两者都没有时，启动会告警，回调里的 `ruleList` 为空。栅格是**彩色**（`local` / `llm` 仍灰度），JPEG 质量默认 85，与栅格流水线重叠，不把整本 JPEG 攒在内存里。

`hunyuan.enabled=false` 或缺少 `baseUrl` / `model` 时，启动告警并回退 `local`（`ForceMode`，不会被 `MINIOCR_OCR_MODE` 再次盖回去）。回退时已经解析好的 DPI 保持不变，所以没写 `dpi` 的 hunyuan 配置回退后仍是 144。服务进程可以先于 vLLM / llama-server 启动；上游连不上时该页请求失败并返回明确的 HTTP 错误，不会把整本栅格堵在队列里。

**vLLM（NVIDIA，官方主路径）** — 按上游 `docs/inference` 装环境，权重用 Hugging Face，不要提交进本仓库：

```bash
vllm serve tencent/HunyuanOCR --host 127.0.0.1 --port 8000
```

```json
"ocr": { "mode": "hunyuan" },
"hunyuan": { "baseUrl": "http://127.0.0.1:8000", "model": "tencent/HunyuanOCR", "concurrency": 2 }
```

`baseUrl` 不要带 `/v1`（写了 `/v1` 也会被剥掉，避免请求变成 `/v1/v1/...`）。显存够可以把 `concurrency` 调高；默认 2。

**llama.cpp（CPU 或消费级 GPU）** — 上游 `docs/llama_cpp.md`，构建 ≥ b11103。转换出语言 GGUF 和 mmproj（可选 DFlash），然后：

```bash
llama-server \
  --model ./HunyuanOCR/hyocr-f16.gguf \
  --mmproj ./HunyuanOCR/mmproj-hyocr-f16.gguf \
  --host 127.0.0.1 --port 8080 --alias HYVL \
  --ctx-size 10240 --n-predict 4096 \
  -fa on --jinja
```

```json
"hunyuan": {
  "baseUrl": "http://127.0.0.1:8080",
  "model": "HYVL",
  "concurrency": 1
}
```

DFlash 时上游建议 `llama-server --parallel 1`，客户端并发也设为 1。apiKey 留空则不发 `Authorization`；需要的话填 `EMPTY` 之类，会作为 Bearer 发送，日志只显示 `(set)`。

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
6. **视觉 OCR 并发**：`llm.ocrConcurrency`（默认 **32**，1–256）；`MINIOCR_LLM_OCR_CONCURRENCY` 可覆盖。调高可缩短墙钟时间，但请留意 **费率与限流**。
7. **视觉 JPEG 质量**：`llm.ocrJpegQuality`（默认 **70**，40–95）；`MINIOCR_LLM_OCR_JPEG_QUALITY` 可覆盖。
8. **`maxCharsPerRequest`（长上下文）**：默认 **300000**（钳制 1000–2_000_000）。DeepSeek Flash 等约 **1M context** 时可设 `200000`–`800000`，减少批次数、一次塞入更多页；注意提供商 **token** 上限（约 1 个中文字 ≈ 1–2 tokens），勿盲目顶满字符上限。
9. **`thinking`（DeepSeek 思考模式）**：DeepSeek Flash / v4 等模型 **默认开启思考**，会拖慢 NER/OCR。本项目默认 **`thinking: false`（关闭）**，请求体会显式发送：
   ```json
   "thinking": { "type": "disabled" }
   ```
   需要开启时设 `"thinking": true` 或 `"enabled"`（亦可 `MINIOCR_LLM_THINKING=1|true|enabled`），将发送 `{ "type": "enabled" }`。配置接受布尔或字符串：`false` / `"disabled"` → disabled；`true` / `"enabled"` → enabled。文本 NER 与视觉 OCR 均会带上该字段。
10. `local` 模式下 LLM **未启用 / 无 key** 时：仅当 `fallbackToHeuristics: true` 才用启发式 NER（默认 **false** → `entities` 为空）。**一旦调用了 LLM NER**（成功为空或失败），**绝不**再静默回退启发式——记错误日志并返回空实体。`ocr.mode=llm` 视觉路径同样只用结构化视觉输出，不用启发式 invent 实体。

### 吞吐旋钮（文件 + 环境变量 / 请求）

| 变量 | 文件字段 | 默认（auto-scale，约 8 核） | 说明 |
| --- | --- | ---: | --- |
| `MINIOCR_OCR_MODE` | `ocr.mode` | **local** | `local`（Paddle）、`llm`（视觉）或 `hunyuan`（本机 HunyuanOCR 服务）；后两者跳过本地模型 |
| `MINIOCR_LLM_OCR_CONCURRENCY` | `llm.ocrConcurrency` | **32**（1–256） | `ocr.mode=llm` 时页级视觉并发 |
| `MINIOCR_LLM_OCR_JPEG_QUALITY` | `llm.ocrJpegQuality` | **70**（40–95） | `ocr.mode=llm` 时页图 JPEG 质量（更低=更快编码/更小上传） |
| `MINIOCR_LLM_THINKING` | `llm.thinking` | **false**（disabled） | DeepSeek 思考模式；`0/1/false/true/disabled/enabled`；默认关闭并显式发送 `thinking.type=disabled` |
| `MINIOCR_ENGINES` | `ocr.engines` | **4**（`Clamp(cores/2, 1, min(16,cores))`） | 页级并行 `PaddleOcrAll` 实例数（仅 local） |
| `MINIOCR_DPI` | `ocr.dpi` | **96**（local）/ **72**（llm）/ **144**（hunyuan）；均仅在未显式设置时 | 栅格化 DPI（也可在 JSON/`?dpi=` 覆盖） |
| `MINIOCR_HUNYUAN_CONCURRENCY` | `hunyuan.concurrency` | **2**（1–64） | `ocr.mode=hunyuan` 页级请求并发；llama-server DFlash 请设 1 |
| `MINIOCR_HUNYUAN_MAX_TOKENS` | `hunyuan.maxTokens` | **8000**（256–16384） | 发给 HunyuanOCR 的 `max_tokens` |
| `MINIOCR_LINE_WORKERS` | `ocr.lineWorkers` | 自动 | 页内 CLS/REC 并行 |
| `MINIOCR_DET_THREADS` | `ocr.detThreads` | 自动 | 检测图内卷积线程 |
| `MINIOCR_USE_CLS` | `ocr.useCls` | **false** | 是否启用方向分类 |
| `MINIOCR_RASTER_WORKERS` | `ocr.rasterWorkers` | 自动（llm / hunyuan：`min(8,cores)`） | 并行 PDF 栅格生产者（封顶 8） |
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
```

健康检查：

```bash
curl -sS http://127.0.0.1:5080/health
```

### 实体抽取（LLM 优先；无 LLM 后启发式可选）

全部页 OCR 完成后抽取 `entities`：

1. **LLM（推荐，`local` 模式文本 NER）**：若 `llm.enabled` 且配置了 `apiKey`，将页文本按 `maxCharsPerRequest`（默认 **300000** 字，钳制至 **2_000_000**）分批，并以 `maxConcurrency`（默认 **8**）为上限**并行**调用 OpenAI 兼容 `POST {baseUrl}/v1/chat/completions`，提示词要求只返回严格 JSON `{"companies":["..."],"persons":["..."]}`（中英均可；禁止臆造正文没有的名字）。各批结果线程安全合并去重，再回扫各页文本填充 `pages` / `count`。
2. **LLM 已调用后**：失败或结果为空时 **不**再回退 `EntityExtractor` 启发式——记错误日志并返回空 `entities`（避免静默启发式人名/公司名污染竞赛结果）。
3. **仅当 LLM 未启用 / 无 key**：若显式 `fallbackToHeuristics: true`，才使用 `EntityExtractor`（Regex + 百家姓 HashSet，`[GeneratedRegex]`，无 ML 包，AOT 安全）；默认 **false** → 空实体。
4. **`ocr.mode=llm`（视觉）**：实体来自页级结构化 `ruleList`（及兼容的 companies/persons 字段），**不用**启发式 invent。

| 类型 | 启发式规则（摘要；仅 `fallbackToHeuristics: true` 且未走 LLM 时） |
| --- | --- |
| 公司名 | 中文组织后缀；英文 Inc/Ltd/Corp/LLC/Co.；标签 `公司名称：` / `甲方：` 等 |
| 人名 | 百家姓 + 职称/标签；英文 `John Smith` 式 |

冒烟：`dotnet run -c Release --project tests/MiniOcr.EntitySmoke`（启发式单元）。无 API key 时服务仍可启动；默认不会静默填启发式实体。

**长上下文提示：** DeepSeek Flash 等约 1M context 时，可将 `maxCharsPerRequest` 设为 `200000`–`800000`，一次请求塞入更多页、减少批次；仍需对照提供商 **token** 限额（中文约 1 字 ≈ 1–2 tokens）。

**局限：** 启发式会漏/误；LLM 依赖模型与 OCR 文本质量，竞赛场景请复核关键实体。

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
3. 若通过 **Release** / `v*` 标签触发，zip 也会尽量挂到该 GitHub Release 上，可直接从 Releases 页下载。Actions 制品只保留 **3 天**（包很大，避免占满 GitHub 存储）；Release 附件不走这个保留期。

**AOT zip：** 解压后含可执行文件 + 原生依赖（`libSkiaSharp` / `pdfium` 的 `.dll` / `.so` / `.dylib`），以及示例 PDF（若打包时存在）；x64 包要求 CPU 满足所选的 SIMD 档位，不满足会在启动时报错退出。  
**单文件 zip：** 解压后通常只有一个 `MiniOcr`（或 `MiniOcr.exe`），拷走即可运行。

## 架构与内存策略

| 环节 | 策略 |
| --- | --- |
| 下载 | `HttpClient`：若 `Accept-Ranges: bytes` 且已知 `Content-Length`，则并行 Range 写入预分配缓冲；否则单流写入预分配/可控增长缓冲。硬顶 **300 MB**。缓冲来自 `ArrayPool<byte>`。 |
| 栅格化 | PDFtoImage（PDFium + SkiaSharp）；**local** 默认 **96 DPI**，**llm** 未显式配置时默认 **72 DPI**，**hunyuan** 未显式配置时默认 **144 DPI**；每 worker **一次** `PdfDocument.Load` + `ToImages`；`AntiAliasing=None`；`local`/`llm` 为 `Grayscale`，`hunyuan` 为彩色；多生产者写入有界 Channel，**绝不**同时持有全部页位图。`llm` / `hunyuan` 默认 raster workers 为 `min(8,cores)`。 |
| OCR | **local**：复用多个 `PaddleOcrAll`（ChineseV6Tiny，默认可关 CLS）；页级引擎池互斥租用；Channel 上 raster↔OCR 重叠。**llm**：不加载 Paddle；**每页** JPEG（质量默认 70）经有界队列立刻交给视觉 worker（`ocrConcurrency`），与栅格重叠——不再等全本编码完才发第一张；优先直接产出 B04/B06 `ruleList`。**hunyuan**：不加载 Paddle；彩色 JPEG 发给本机 HunyuanOCR（官方文档解析提示词，`max_tokens` 默认 8000），页文本再走与 local 相同的 NER。 |
| 实体 | 优先 `LlmEntityExtractor`（Chat Completions 分批）；失败/关闭则 `EntityExtractor` 启发式。 |
| JSON | 源生成 `AppJsonContext`，AOT 友好。 |

### 峰值内存（量级，非承诺值）

- PDF 本体：最多约 **300 MB**（池化租用）
- 在途页位图：窗口内数页（DPI 越低越小）
- 模型 + 推理工作区：随引擎数上升；关闭 CLS 可明显降低
- 设计目标：2000 页时内存不随页数线性涨到「整本位图」，而随 **窗口 + 模型** 近似封顶

## 依赖

| 包 | 说明 |
| --- | --- |
| `Sdcb.SimdPaddleOCR` 1.4.1 | 纯托管 PP-OCRv6 |
| `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` | 中文 tiny DET+REC（CLS 可选） |
| `PDFtoImage` 5.4.0 | PDFium 栅格化（SkiaSharp） |

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `POST` | `/challenge` | **竞赛 serviceUrl（推荐）**：异步受理，见上文「竞赛协议」 |
| `POST` | `/` | 与 `/challenge` 相同（可将 serviceUrl 填根路径） |
| `GET` | `/health` | 健康、模型与当前旋钮 |
| `POST` | `/ocr` | 调试用同步 OCR：竞赛兼容 `files[{fileId,url}]`，响应同回调 `result` 形状（遗留 `{url,dpi?}` 仍可用） |
| `GET` | `/` | 纯文本接口说明 |

## 项目结构

```
miniocr/
  MiniOcr.csproj          # Web + PublishAot + IlcInstructionSet=avx2（仅 x64）；可选 MiniOcrSingleFile
  .github/workflows/publish.yml  # 多平台 AOT + linux/win 单文件矩阵
  Program.cs              # SlimBuilder + /challenge /ocr /health
  AppJsonContext.cs       # AOT JSON
  Models/OcrModels.cs
  Services/
    AppConfigStore.cs     # config path resolution (AppData / Application Support / ~/.config)
    OcrRuntimeConfig.cs   # 文件+环境变量+CPU 自动扩缩
    LlmEntityExtractor.cs # OpenAI 兼容 Chat Completions NER
    ParallelPdfDownloader.cs
    RentedBuffer.cs
    OcrEngine.cs
    HunyuanVisionOcr.cs  # ocr.mode=hunyuan → 本机 vLLM / llama-server
    PdfOcrPipeline.cs
    EntityExtractor.cs    # 启发式回退
    ChallengeJobService.cs # 竞赛异步队列 + 回调
    ChallengeResultMapper.cs # 按页 B04/B06 + originText
  tests/MiniOcr.EntitySmoke/  # 实体抽取冒烟
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
