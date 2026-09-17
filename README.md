# Unity AIGC Toolchain

面向 Unity Editor 的本地 AIGC 游戏资产生成插件。它将提示词扩写、概念图生成、人工预览确认、图像转 3D 和 Unity 资产导入串联为一套非阻塞工作流。

```text
中文或英文描述
    -> Qwen 提示词扩写
    -> ComfyUI / Z-Image Turbo 概念图
    -> Unity 预览与确认
    -> GameAssetAIStudio / Hunyuan3D 2.1
    -> GLB + OBJ + Manifest + Quality Report
    -> Unity Prefab 与可编辑预览材质
```

## Related Repository

3D 生成、质量检查和资产打包由独立的 Python 服务完成：

- [GameAssetAIStudio](https://github.com/IrisOnMars/GameAssetAIStudio)

本仓库负责 Unity 编辑器交互和 HTTP 客户端；服务端仓库负责 ComfyUI/Hunyuan3D 调度。两者通过本地 REST API 解耦，可以分别部署和替换。

## Features

- Unity Editor 一键式资产生成窗口
- Character、Creature、Prop 分类提示词约束
- 本地 Qwen OpenAI-compatible 服务自动启停
- 基于 `UnityWebRequest` 的非阻塞请求、轮询、超时和错误处理
- ComfyUI 工作流提交、状态查询与图片下载
- 概念图预览、重新生成和 3D 生成确认
- GameAssetAIStudio REST API 集成
- 自动导入 PNG、GLB、OBJ、Manifest 和 Quality Report
- 自动创建可编辑 Lit 材质与 Prefab

## Requirements

- Unity 2021.3 或更高版本
- 可访问的 ComfyUI 服务，默认地址为 `http://127.0.0.1:8188`
- Python 环境以及 `torch`、`transformers`
- 本地 Qwen Instruct 模型
- 需要 3D 输出时：运行中的 GameAssetAIStudio 服务和 Hunyuan3D 2.1 checkpoint

模型权重、ComfyUI 本体、Python 虚拟环境和生成结果均不包含在本仓库中，需要使用者自行安装或下载。

## Installation

可以通过 Unity Package Manager 的 **Add package from git URL** 安装：

```text
https://github.com/IrisOnMars/Unity_AIGC_Tool.git
```

也可以将仓库放入 Unity 项目的 embedded package 目录：

```text
<UNITY_PROJECT>/Packages/com.aigc.toolchain/
```

安装后从 Unity 菜单打开：

```text
AIGC Tool -> Asset Generator
```

## Local Configuration

首次运行前，请在插件窗口的 **Settings** 中按本机环境修改以下配置：

| Setting | Description | Default |
|---|---|---|
| Qwen Endpoint | OpenAI-compatible Chat Completions 地址 | `http://127.0.0.1:8000/v1/chat/completions` |
| Qwen Python | 用于启动本地 Qwen 服务的 Python 可执行文件 | 本机 Python 路径 |
| Qwen Server Script | `Tools/qwen_openai_server.py` 的实际路径 | 当前 Package 下的脚本 |
| ComfyUI Base URL | ComfyUI HTTP 地址 | `http://127.0.0.1:8188` |
| Studio API Base URL | GameAssetAIStudio REST API 地址 | `http://127.0.0.1:7861` |
| Studio Project Root | GameAssetAIStudio 的本地克隆目录 | 需要按本机修改 |
| Studio Python | GameAssetAIStudio 虚拟环境中的 Python | 需要按本机修改 |

本地 Qwen 服务脚本中的模型目录也需要修改：

```python
# Tools/qwen_openai_server.py
MODEL_PATH = r"<QWEN_MODEL_DIR>"
```

`<QWEN_MODEL_DIR>` 应指向包含模型配置和权重文件的本地目录。请勿将模型权重或包含凭据的配置提交到 Git。

## ComfyUI Workflow

`Editor/single_image_api.json` 是默认 API 工作流模板，其中：

- `__PROMPT__` 会被替换为扩写后的正向提示词
- `__NEGATIVE_PROMPT__` 会被替换为按资产类型生成的负面提示词
- 工作流引用的模型文件名必须与本机 ComfyUI 中的文件名一致

默认工作流使用 Z-Image Turbo、Qwen 文本编码器和 Flux VAE。若模型名称或节点版本不同，请重新从 ComfyUI 导出 API workflow 并更新模板。

## Usage

1. 启动 ComfyUI。
2. 启动 GameAssetAIStudio API，或在 Unity 中启用 `Auto Start Studio API`。
3. 输入资产描述并选择 `Asset Type`，也可以使用 `Auto` 自动判断。
4. 点击 `Generate 3D Asset`。
5. 检查概念图；可以重新生成，或确认后继续生成 3D。
6. 产物会导入到 `Assets/AIGC_Generated/<asset_id>/`。

每个 3D 资产目录通常包含：

```text
concept.png
concept_request.json
model.glb
model.obj
model.prefab
AIGC_Preview.mat
manifest.json
quality_report.json
```

## Project Layout

```text
Editor/AIGCWindow.cs               Unity 编辑器窗口与流程控制
Editor/PromptExtender.cs           Qwen 请求、提示词约束及服务生命周期
Editor/ComfyClient.cs              ComfyUI 请求、轮询和文件下载
Editor/GameAssetStudioClient.cs    3D 服务客户端及任务轮询
Editor/single_image_api.json       默认概念图 API 工作流
Tools/qwen_openai_server.py        本地 OpenAI-compatible Qwen 服务
package.json                       Unity Package 描述文件
```

## Current Limitations

- 当前 3D 阶段主要生成几何网格，不保证包含可直接使用的 UV、PBR 纹理或骨骼。
- AI 生成的拓扑、面数、比例和武器结构仍需人工检查。
- 生产使用前建议增加减面、UV、纹理烘焙、LOD、Rig 和碰撞体处理。
- 本项目默认面向本地单用户开发环境，未实现公网服务所需的认证、限流和隔离机制。
