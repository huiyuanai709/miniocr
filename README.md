# MiniOCR

基于 [SimdPaddleOCR](https://github.com/huiyuanai709/SimdPaddleOCR)（上游 [sdcb/SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR)）的 **Native AOT** PDF OCR HTTP 服务（**.NET 11 RC / `net11.0`**）。

从 URL 下载 PDF（不超过 300 MB），按页栅格化并识别，返回每页文本。可选调用 OpenAI 兼容接口做多模态识别，或从文本中抽取人名和公司名。

**`ocr.mode`：**

- **`local`（默认）**：本地 PP-OCRv6 **ChineseV6Small**。人名和公司名可交给 LLM，也可在配置里打开启发式回退。
- **`llm`**：不加载本地 Paddle 模型。每页 JPEG 以 `image_url` 发给多模态 Chat Completions。
- **`wechat`（实验，仅 Windows x64）**：调用本机已安装的微信 OCR 插件。非官方接口，只适合在自己的电脑上对比，不要当成对外服务。

## 环境要求

- Windows / Linux / macOS（x64 与 ARM64）。CI 产出多平台 Native AOT 包
- [.NET 11 RC SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- Native AOT 需要本机 C 工具链（`gcc` / `clang` 以及 zlib）
- **x64** 默认 **AVX2**，另有 **AVX-512** 包；**ARM64** 使用 NEON 基线

### 安装 .NET 11 RC（Linux）

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
dotnet --list-sdks
```

## 构建与运行

```bash
git clone --recurse-submodules https://github.com/huiyuanai709/miniocr.git
cd miniocr
# 已有工作区缺子模块时：git submodule update --init --recursive
dotnet publish -c Release -r linux-x64 -o ./artifacts/linux-x64
```

换指令集档位（默认 `avx2`）：

```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:IlcInstructionSet=avx512v2 -o ./artifacts/linux-x64-avx512v2
```

产物需要和原生库放在同一目录：

| 文件 | 说明 |
| --- | --- |
| `MiniOcr` | Native AOT 可执行文件 |
| `libSkiaSharp.so` | Skia（Windows / macOS 为 `.dll` / `.dylib`） |
| `libpdfium.so` | PDFium（后缀同上） |

缺原生库时，栅格化会失败。`PublishSingleFile` 与 `PublishAot=true` 不能同时得到真正的单文件；需要单文件时用下面的非 AOT 模式。

```bash
cd artifacts/linux-x64
./MiniOcr --urls http://0.0.0.0:5080
```

开发模式：

```bash
dotnet run -c Release --urls http://127.0.0.1:5080
```

### 可选：单文件包（非 AOT）

首次运行会把原生库解压到 `$HOME/.net/MiniOcr/<hash>/`。x64 上由 JIT 在运行时探测 AVX2 / AVX-512。

```bash
dotnet publish -c Release -r linux-x64 -o ./artifacts/linux-x64-singlefile \
  -p:MiniOcrSingleFile=true
```

## 配置

优先级：环境变量 `MINIOCR_*` 覆盖 `config.json`。OCR 项为 `null` 且 `autoScaleFromCpu` 为 true 时，按 CPU 核数填写。LLM 的 key 不会写入日志，只显示 `(set)` 或 `(empty)`。

**查找顺序：**

1. `MINIOCR_CONFIG_PATH`（具体文件）
2. 下表里已经存在的文件
3. 否则在规范位置写入示例 `config.json`

| 平台 | 路径 |
| --- | --- |
| Windows | `%APPDATA%\MiniOcr\config.json` |
| macOS | `~/Library/Application Support/MiniOcr/config.json`（也读 `~/.config/MiniOcr/config.json`） |
| Linux | `~/.config/MiniOcr/config.json` |

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
    "renderMode": "parallel",
    "textLayer": "auto",
    "renderProcesses": null,
    "useCls": false,
    "removeRedSeal": true,
    "backend": "cpu",
    "vulkanDevice": "",
    "recIntraOpThreads": 1,
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

`baseUrl` 不要带 `/v1` 后缀，客户端请求 `{baseUrl}/v1/chat/completions`。`llm` 模式需要 `enabled`、非空 `apiKey`、`baseUrl` 和 `model`，否则启动时回退到 `local`。

`wechat` 只在 Windows x64 上查找本机插件（`WeChatOCR.exe` 或 `wxocr.dll`，以及同目录的 `mmmojo_64.dll`）。路径留空时按常见安装位置和卸载注册表查找。找不到插件时默认回退 `local`。

### 环境变量

| 变量 | 文件字段 | 默认 | 说明 |
| --- | --- | --- | --- |
| `MINIOCR_OCR_MODE` | `ocr.mode` | `local` | `local`、`llm` 或 `wechat` |
| `MINIOCR_DPI` | `ocr.dpi` | local 96 / llm 72 | 栅格 DPI。请求里可用 `?dpi=` 或 `"dpi"` |
| `MINIOCR_ENGINES` | `ocr.engines` | 按核数 | 页级 `PaddleOcrAll` 个数（仅 local） |
| `MINIOCR_LINE_WORKERS` | `ocr.lineWorkers` | 按核数 | 页内识别并行 |
| `MINIOCR_DET_THREADS` | `ocr.detThreads` | 按核数 | 检测卷积线程 |
| `MINIOCR_REC_INTRA_OP_THREADS` | `ocr.recIntraOpThreads` | 1 | 每个引擎的识别线程。`0` 交给库分配 |
| `MINIOCR_REC_BATCH` | — | 8 | `RecBatchLines` |
| `MINIOCR_DET_LIMIT_SIDE` | — | 960 | 检测边长上限 |
| `MINIOCR_USE_CLS` | `ocr.useCls` | false | 方向分类 |
| `MINIOCR_REMOVE_RED_SEAL` | `ocr.removeRedSeal` | true | 彩色页在检测前去掉高饱和红章，换成页角纸色或白。灰度页跳过。`0` / `false` / `off` 关闭，并回到 Gray8 渲染 |
| `MINIOCR_OCR_BACKEND` | `ocr.backend` | `cpu` | `cpu` / `auto` / `vulkan` / `metal`。`metal` 仅 macOS |
| `MINIOCR_OCR_VULKAN_DEVICE` | `ocr.vulkanDevice` | 空 | 设备序号或名称子串 |
| `MINIOCR_RASTER_WORKERS` | `ocr.rasterWorkers` | 按核数 | 进程内栅格线程。`parallel` 时用于回退 |
| `MINIOCR_RENDER_MODE` | `ocr.renderMode` | `parallel` | `parallel` 或 `inprocess` |
| `MINIOCR_RENDER_PROCESSES` | `ocr.renderProcesses` | 1–4 | 并行渲染进程数 |
| `MINIOCR_OCR_TEXT_LAYER` | `ocr.textLayer` | `auto` | `auto` / `off` / `force`。有可用文本层时跳过识别 |
| `MINIOCR_TEXT_LAYER_MIN_CHARS` | `ocr.textLayerMinChars` | 40 | 文本层最短非空白字符 |
| `MINIOCR_TEXT_LAYER_MAX_UNKNOWN_RATIO` | `ocr.textLayerMaxUnknownRatio` | 0.02 | 未知字符比例上限 |
| `MINIOCR_TEXT_LAYER_IMAGE_COVERAGE` | `ocr.textLayerImageCoverage` | 0.55 | 视为扫描页的图片面积占比 |
| `MINIOCR_TEXT_LAYER_IMAGE_MIN_CHARS` | `ocr.textLayerImageMinChars` | 200 | 扫描页或隐形文本层的最短字符 |
| `MINIOCR_LLM_API_KEY` | `llm.apiKey` | 空 | 覆盖文件中的 key |
| `MINIOCR_LLM_BASE_URL` | `llm.baseUrl` | — | 覆盖 base URL |
| `MINIOCR_LLM_MODEL` | `llm.model` | — | 覆盖模型名 |
| `MINIOCR_LLM_MAX_CONCURRENCY` | `llm.maxConcurrency` | 8 | 文本抽取并发 |
| `MINIOCR_LLM_PAGES_PER_REQUEST` | `llm.pagesPerRequest` | 10 | 每个文本请求的非空页数 |
| `MINIOCR_LLM_OCR_CONCURRENCY` | `llm.ocrConcurrency` | 32 | 视觉识别页级并发 |
| `MINIOCR_LLM_OCR_JPEG_QUALITY` | `llm.ocrJpegQuality` | 70 | 视觉 JPEG 质量（40–95） |
| `MINIOCR_LLM_THINKING` | `llm.thinking` | false | 思考模式。默认发送 `thinking.type=disabled` |
| `MINIOCR_LLM_JSON_OBJECT` | `llm.jsonObject` | true | `response_format.type=json_object` |
| `MINIOCR_WECHAT_OCR_PATH` | `ocr.wechatOcrPath` | 空 | 插件可执行文件或 dll |
| `MINIOCR_WECHAT_DIR` | `ocr.wechatDir` | 空 | 含 `mmmojo_64.dll` 的目录 |
| `MINIOCR_WECHAT_INSTANCES` | `ocr.wechatInstances` | 1–3 | 插件进程数 |
| `MINIOCR_WECHAT_FALLBACK` | `ocr.wechatFallbackToLocal` | true | 为 false 时找不到插件则退出 |

未设置的引擎数、行内线程和检测线程按逻辑核数估算。显式环境变量或文件值优先。小显存 GPU 上引擎数会降为 1。

## HTTP

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/health` | 进程、配置路径和当前 OCR 开关 |
| `POST` | `/ocr` | 同步识别。JSON 里给 `url`，或 `files`，或本机 `path` |
| `POST` | `/ocr/upload` | `multipart` 上传一个 PDF |
| `GET` | `/` | 纯文本接口说明 |

```bash
python3 -m http.server 8000 --directory samples

curl -sS http://127.0.0.1:5080/health

curl -sS -X POST 'http://127.0.0.1:5080/ocr?dpi=96' \
  -H 'Content-Type: application/json' \
  -d '{"url":"http://127.0.0.1:8000/sample-multipage.pdf"}'

curl -sS -X POST http://127.0.0.1:5080/ocr/upload \
  -F "file=@samples/sample-multipage.pdf;type=application/pdf"
```

`?verbose=1` 时响应带有规则的页的文本、耗时和 `ruleList`，没有规则的页不出现，页码保持不变。本机文件用 `{"path":"/absolute/path/to/file.pdf"}`。

## SIMD 档位（x64 Native AOT）

指令集在编译期写入二进制。默认 `avx2`（Haswell 及以后）。不设档位时会退回 SSE2，识别会变慢。

| 包名后缀 | `IlcInstructionSet` | CPU |
| --- | --- | --- |
| （无） | `avx2` | 通用 x64 |
| `-avx512v2` | `avx512v2` | 支持 AVX-512 的较新 x64 |

CPU 不满足档位时进程会退出，并提示缺少指令集，此时改用无后缀包。ARM64 不设置 `IlcInstructionSet`。macOS 只发布 `osx-arm64`。

## 发布包

推送到 `main`、手动运行 **Publish Native AOT**，或发布 GitHub Release / `v*` 标签时，[`.github/workflows/publish.yml`](.github/workflows/publish.yml) 会上传各平台 zip。也可从 Releases 页下载。

- `miniocr-win-x64` / `miniocr-linux-x64`：AOT，AVX2
- `miniocr-win-x64-avx512v2` / `miniocr-linux-x64-avx512v2`：AOT，AVX-512
- `miniocr-osx-arm64` / `miniocr-linux-arm64`：AOT，NEON
- `miniocr-win-x64-singlefile` / `miniocr-linux-x64-singlefile`：单文件，非 AOT

AOT 包请整目录保留可执行文件和 `libSkiaSharp` / `pdfium`。

## 依赖

| 来源 | 说明 |
| --- | --- |
| `external/SimdPaddleOCR` | fork 的 `ProjectReference`，含 ChineseV6Small。Apache-2.0 |
| `external/PDFtoImage` | fork 的 `ProjectReference`（含 `PDFtoImage.Parallel`）。MIT |

## Docker / 8×GPU

一台机器上跑 8 个进程，每个进程绑一张 NVIDIA GPU。需要已安装 [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html) 的 Linux，以及 Docker Compose v2。

`gpu0` 是协调进程，同时自己也做识别，监听 `5080`。`gpu1`–`gpu7` 是工作进程，端口 `5081`–`5087`。容器之间用服务名互相访问（`http://gpu0:5080` 等）。

主机上的卡号由 compose 里的 `device_ids` / `NVIDIA_VISIBLE_DEVICES` 决定。容器里只能看到这一张卡，所以每个进程的 `MINIOCR_OCR_VULKAN_DEVICE` 都是 `0`，不要写成 `1`–`7`。

默认镜像基于 Ubuntu 24.04（运行时需要 glibc 2.38 和 `libicu74`）。`Dockerfile` 在构建阶段用仓库源码做 `linux-x64` Native AOT 发布，指令集是 AVX-512（`avx512v2`），再把产物放进运行镜像，并带上 Vulkan loader、字体和 PDFium/Skia 依赖。构建前要初始化子模块，否则缺少 `external/SimdPaddleOCR` 和 `external/PDFtoImage`。密钥只从环境变量进入进程，不写进镜像：

| 变量 | 作用 |
| --- | --- |
| `MINIOCR_LLM_API_KEY` | 覆盖 `config.docker.json` 里空的 `llm.apiKey` |
| `MINIOCR_CLUSTER_TOKEN` | 覆盖空的 `cluster.token`。为空时集群保持关闭 |

`llm.timeoutSeconds`、`maxCharsPerRequest`、`fallbackToHeuristics` 没有对应环境变量，写在 `config.docker.json`。OCR 为 Vulkan、4 个引擎、DPI 96、关闭方向分类。识别批大小不设 `MINIOCR_REC_BATCH`，24GB 显卡会保持较大的批。8 个进程各自的 `maxConcurrency` 是 16；出口压力大时用 `MINIOCR_LLM_MAX_CONCURRENCY` 调低。

上传的 PDF 没有可再次下载的地址时，协调进程把它写到 `MINIOCR_CLUSTER_SHARED_DIR`（compose 里是卷 `miniocr-jobs`，挂到 `/var/lib/miniocr/jobs`）。同机的工作进程直接打开这个文件。路径不存在或长度对不上时，仍向协调进程下载。任务结束后删除该文件；渲染进程先放开对它的映射。别的机器上看不到这个路径，所以还是走下载。

```bash
git submodule update --init --recursive
cp .env.example .env
# 编辑 .env，填入上面两个变量，不要把 .env 提交进仓库

docker compose build
docker compose up
```

健康检查：`curl -sS http://127.0.0.1:5080/health`（工作进程把端口换成 `5081`–`5087`）。GPU 预热较慢，compose 的 `start_period` 是 180 秒。

镜像里的二进制带 AVX-512。CPU 没有该指令集时进程会直接退出，改回 AVX2 再构建：

```bash
docker compose build --build-arg ILC_INSTRUCTION_SET=avx2
```

`Dockerfile.publish` 与 `Dockerfile` 是同一套源码发布：

```bash
docker build -f Dockerfile.publish -t miniocr:local .
```

## 许可证

示例与本仓库代码以仓库为准。SimdPaddleOCR 及其模型遵循上游 Apache-2.0。PDFtoImage 遵循其 MIT 许可证。
