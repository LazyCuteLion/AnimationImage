# AnimationImage

> **基于 SkiaSharp 的 WPF & Avalonia 极简动图播放方案**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## 🚀 简介

`AnimationImage` 是一个专为 **WPF** 和 **Avalonia UI** 打造的动图解决方案。基于 **SkiaSharp** ，性能优秀。

它支持播放 **Lottie (JSON)**、**GIF** 和 **WebP** 格式，相比现有方案，具有更高的帧率、更佳的渲染性能以及更低的内存占用。

### ✨ 核心特性

*   **多平台支持**：支持 WPF 和 AvaloniaUI（目前未对移动端进行测试）。
*   **多格式兼容**：支持 Lottie、GIF、WebP 动画格式。
*   **极致性能**：动图利用SKCodec流式加载，逐帧解码。Lottie则利用Skottie，极致流畅。
*   **高帧率体验**：WPF支持强制帧率，当硬件支持时，可以达到更高的帧率（而非默认的60）。AvaloniaUI则默认以最高帧率运行，无需此设置。
*   **极简 API**：通过附加属性或标记扩展，即可集成，使用原生Image控件作为渲染器：` <Image ani:AnimationBehavior.AnimatableBitmap="[path]"/>`
*   **灵活控制**：支持预加载帧数配置、自动播放、循环次数控制等。

---

## 📦 安装

通过 NuGet 包管理器安装：

```bash
# WPF 版本
Install-Package AnimationImage.WPF

# Avalonia 版本
Install-Package AnimationImage.Avalonia