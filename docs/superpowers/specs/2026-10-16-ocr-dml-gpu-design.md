# OCR 引擎迁移到 DirectML GPU（免 CUDA）调研 + 设计

## 背景与问题

F9 翻译开启时 CPU 飙高：有翻译内容时整机约 55%（多数核 50%+），无内容时约 5%。
根因是 OCR（onnxruntime CPU 版）多核多线程跑满；帧转换是单线程，不是元凶。

文本闪烁问题已单独修复（`LineStabilizer` 改按位置匹配 + 变更检测用编辑距离，见
`src/ScrTranslator.Core/Translation/LineStabilizer.cs`、`src/ScrTranslator.App/TranslationEngine.cs`），
本 spec 只处理**遗留的 CPU 飙高**。

用户硬件：RTX 4090（sm89），CUDA 12.0 + 12.9 已装，20 核 CPU。目标：把 OCR 甩到
GPU，CPU 基本不动，**且免装 CUDA/cuDNN/TensorRT**（要"像 Vulkan 那样即插即用"）。

## 硬约束

- **OCR 必须支持韩文**（SC:R 韩→中）。PP-OCRv6 已砍韩文，所以**必须停留在
  PP-OCRv5 韩文模型**（`korean_PP-OCRv5_mobile_rec`）。任何不支持韩文的引擎直接排除。
- 不动 `IOcrEngine` 接口、不动闪烁修复（LineStabilizer + 编辑距离）。
- 用户明确要求**不再折腾线程**，要换引擎，不是加参数。

## 模型身份核实（决定性证据）

当前 `models\`（项目根，git 跟踪，拷进 bin）是**整套干净的 PP-OCRv5 mobile 韩文管线**，
不是混装。onnx 输入/输出维度全部对上 v5 mobile 标准：

| 模型 | 输入 | 输出 | 身份判定 |
|---|---|---|---|
| `det.onnx` (4.83 MB) | `[N,3,H,W]` 动态 | DB 检测概率图 | PP-OCRv5 mobile 检测器 `ch_PP-OCRv5_mobile_det` |
| `cls.onnx` (0.59 MB) | `[N,3,H,W]` | `[N,2]`（0°/180°） | PP-OCRv5 mobile 方向分类 `ch_ppocr_mobile_v2.0_cls_infer` |
| `rec.onnx` (13.4 MB) | `[N,3,48,W]` 动态宽 | `[N,T,11947]` | PP-OCRv5 mobile **韩文**识别 `korean_PP-OCRv5_mobile_rec` |

决定性证据：`rec.onnx` 输出最后一维 **11947 = 字典 11945 行 + 2（CTC 空白 + 结束符）**，
证明 rec 就在 `dict.txt`（11945 行，93% 谚文）上训练。det/cls/rec 全是 v5 mobile，
唯一特别的是 rec 用韩文版（非 v5 备份里的 Latin 7.5 MB 版）。

## OCR 引擎选项调研（逐家排除）

| 引擎 | 韩文 | GPU | 排除理由 |
|---|---|---|---|
| **RapidOcrNet**（现状） | ✅ | CPU（onnxruntime） | onnxruntime CPU 多核拉满；本 spec 就是把它换到 DML |
| **PaddleSharp**（Sdcb.PaddleInference） | ✅（v5 原生） | GPU=CUDA+cuDNN 9.10+TensorRT 加 PATH；CPU=MKL 全核 | GPU 重装、CPU 多线程；用户嫌重且不想折腾 |
| **SimdPaddleOCR**（PP-OCRv6） | ❌ | Vulkan | ① 自带字典 0 谚文（`ppocr_keys.txt` 6904 行、`rec_keys.txt` 18708 行全是中+拉丁+符号）；② `docs/v5_rec.md` 明确 v5 多语言 rec "Model.Load 能载、Recognize 动态形状推理崩、多语言未支持"；③ 字典与模型耦合（输出层维=字典大小、在那字典上训练），不能单加韩文字典；唯一韩文模型是 PP-OCRv5（它推不动）或自训 v6 韩文（不现实） |
| **PaddleOCR 官方** | ✅（v5） | **只有 CUDA** | 全仓（含代码）搜 `vulkan`=0、`DirectML`/`DmlExecutionProvider`=0。推理引擎三类：`paddle`（飞桨+TensorRT CUDA）、`transformers`（CUDA）、`onnxruntime`（`CUDAExecutionProvider`/`CPUExecutionProvider`，`providers` 透传但 DML 无文档无测试不保证）。且是 Python 框架，嵌 .NET WPF 要走子进程/SDK，成本高 |

**PP-OCRv6 砍韩文**（官方源：PaddleOCR GitHub `PP-OCRv6.md`、v3.7.0 release、HuggingFace
`PaddlePaddle/pp-ocr6`、arxiv 2606.13108 Table 10、paddleocr.ai）：v6 medium/small 50 语种
（简/繁中、英、日 + 46 拉丁），**无韩文**。韩文在 v5（`korean_PP-OCRv5_mobile_rec`），v6 移除。

## 决策：用 onnxruntime 的 DirectML

**DirectML（`DmlExecutionProvider`）** 是 onnxruntime 的执行提供者，Windows 下任何
DirectX 显卡（含 4090）直接跑，**免装 CUDA/cuDNN/TensorRT**——即用户要的"像 Vulkan
那样即插即用的简单 GPU"。

> 关键澄清：这个"简单 GPU"是 **onnxruntime 的能力**，不是 PaddleOCR 的。用户纠结的
> "PaddleOCR 自己"走不通简单 GPU（它只有 CUDA）。而 RapidOcrNet 底层本就是 onnxruntime，
> 所以 DirectML 天然契合——模型、字典一行不动，只换执行提供者。

## DirectML 实证（onnxruntime 1.24.4 DirectML，D:\Temp\opencode\dmltest）

三个模型**全部能在 DML 加载并跑通**（det/rec/cls 各跑一次假输入，DML vs CPU 对照）：

| 模型 | DML | CPU | 结论 |
|---|---|---|---|
| det (1×3×320×320) | 3 ms | 12 ms | 完全 GPU，快 4 倍 |
| rec (1×3×48×320) | 4 ms | 6 ms | 比 CPU 快，**部分 shape 算子回退 CPU** |
| cls (1×3×80×320) | 1 ms | 1 ms | 模型太小，无感 |

rec 回退告警（onnxruntime 标准行为）：
`Some nodes were not assigned to the preferred execution providers ... ORT explicitly
assigns shape related ops to CPU to improve perf.`
即 rec 的**动态形状算子**被 onnxruntime 主动放 CPU 提速，主干（注意力/卷积）仍走 DML。
所以**不是绝对零 CPU**，但绝大部分甩到 4090。

## 关键发现：RapidOcrNet 不必绕开

反射 `RapidOcrNet.dll` (2.0.0) 发现它**自带** `RapidOcr.InitModels(det, cls, rec, keys,
SessionOptions)` 重载（`TextDetector`/`TextRecognizer`/`TextClassifier` 也各有
`InitModel(path, SessionOptions)`）。**不用重写 PP-OCR 管线**——传一个带 DML 的
`SessionOptions` 进去即可。`SessionOptions.IntraOpNumThreads` 能限住回退 CPU 算子的
线程数，正好治 CPU 问题。

## 打包约束（DLL 冲突）

`RapidOcrNet` 2.0.0 的 nuspec 依赖：`Microsoft.ML.OnnxRuntime`（**原生 CPU**）1.24.3、
`Microsoft.ML.OnnxRuntime.Managed` 1.24.3、`Clipper2` 2.0.0、`SkiaSharp` 3.119.1。
RapidOcrNet 包内**只有托管 DLL，不含原生 onnxruntime.dll**（原生由传递依赖提供）。

加 `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 会引入 DML 版原生 `onnxruntime.dll`，
与 RapidOcrNet 传递来的 CPU 版同名冲突。处置：**排除原生 CPU 包的 runtime 资产**，只留
DML 版；托管 API 仍由 Managed 包提供（1.24.3→1.24.4 向上 roll-forward，.NET 允许）。

## 目标 / 非目标 / 反目标

**目标**
- OCR 走 DirectML GPU，CPU 基本不动（rec 的回退算子线程被限）。
- 韩文识别能力不变（同一套 PP-OCRv5 模型）。
- 免装 CUDA/cuDNN/TensorRT。

**非目标**
- 不动 `IOcrEngine` 接口、不动 `App.xaml.cs` 的 DI 传参（`OcrThreads` 自然映射
  `IntraOpNumThreads`）。
- 不动 LineStabilizer / 编辑距离（闪烁修复保留）。
- 不改模型文件与字典。

**反目标（做错了会怎样）**
- 用 CUDA/TensorRT → 重装、用户明确不要。
- 用 PaddleSharp/SimdPaddleOCR → 重装 / 无韩文。
- 绕开 RapidOcr 重写 PP-OCR 管线 → 不必要，RapidOcr 已支持 SessionOptions。
- 忘了排除 CPU 版原生 onnxruntime → 两个 `onnxruntime.dll` 影子，DML 不生效。

## 设计

两处改动：

### 1. `src/ScrTranslator.Core/ScrTranslator.Core.csproj`

```xml
<PackageReference Include="RapidOcrNet" Version="2.0.0" />
<!-- 新增：DirectML 执行提供者 + DML 版原生 onnxruntime -->
<PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.24.4" />
<!-- 排除 RapidOcrNet 传递依赖的 CPU 版原生 onnxruntime，避免同名 DLL 冲突；
     托管 API 仍由 Microsoft.ML.OnnxRuntime.Managed 提供 -->
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.24.3" ExcludeAssets="runtime" />
```

### 2. `src/ScrTranslator.Core/Ocr/PaddleOcrEngine.cs`

构造 `SessionOptions` 走 DML，把线程数映射到 `IntraOpNumThreads`：

```csharp
using Microsoft.ML.OnnxRuntime;   // 新增

// 构造函数内，文件检查之后：
var so = new SessionOptions();
so.AppendExecutionProvider_DML(0);                 // DirectML GPU（设备 0 = 4090），免装 CUDA
so.IntraOpNumThreads = Math.Max(1, numThreads);    // 限住回退 CPU 算子的线程，避免拉满全核
_ocr = new RapidOcr();
try { _ocr.InitModels(detPath: det, clsPath: cls, recPath: rec, keysPath: keys, sessionOptions: so); }
catch { _ocr.Dispose(); throw; }
```

`App.xaml.cs` 的 DI 行不变（`SettingsHolder.Current.Ocr.OcrThreads` 继续传 `numThreads`）。

## 风险 / 注意

- **rec 非绝对零 CPU**：shape 算子回退，但线程被 `IntraOpNumThreads` 限住，不会再拉满 20 核。
- **版本 1.24.3→1.24.4**：RapidOcrNet 针对 Managed 1.24.3 构建，加载 1.24.4 是向上 roll-forward，.NET 允许；原生 `onnxruntime.dll` 只有 DML 1.24.4 一份，Managed 与原生版本一致。
- **DLL 排除必须正确**：CPU 版 `onnxruntime.dll` 若残留在输出目录会影子 DML 版，导致 DML 不生效。
- **必须实机验证**：DLL 排除 / 版本 roll-forward 这种事，编译过 ≠ 运行时 OK。改完 `taskkill`
  后重构建，F9 实机看 OCR 正常 + CPU 下降。
