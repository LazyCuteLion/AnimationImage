# AnimationImage.WinUI

> **基于 SkiaSharp + D3D12 的 WinUI 极简动图播放方案**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![GitHub Repo](https://img.shields.io/badge/GitHub-Repo-blue?logo=github)](https://github.com/LazyCuteLion/AnimationImage)


## 🚀 简介

**AnimationImage.WinUI** 支持播放 **Lottie(JSON)、GIF、WebP、APNG** 格式，通过 Composition API 直接渲染，Lottie 默认走 D3D12 GPU 加速管线（零拷贝直渲到合成层），动图走 CPU 解码 + Win2D 呈现，兼顾性能与兼容性。

### ✨ 核心特性

*   **GPU 加速**：Lottie 走 D3D12 + Skia GPU 管线，复杂矢量动画丝滑流畅。
*   **三级降级**：GPU 零拷贝 ↘ GPU 光栅+ReadPixels ↘ 纯 CPU 光栅，自动降级无需干预。
*   **多格式**：Lottie、GIF、WebP、APNG 一站式支持。
*   **智能缓存**：MMF 内存映射，大分辨率动图无压力播放，跨实例复用。
*   **Composition 原生**：通过 `SpriteVisual` + `CompositionDrawingSurface` 呈现，支持任意 `FrameworkElement` 作为宿主。
*   **全可控**：自动播放、循环次数、进度跳转、`Play/Pause/Stop` 命令，开箱即用。

---

## 🚝 使用方法

引入命名空间：`xmlns:ani="using:AnimationImage"`

```xaml
<!-- 宿主元素可以是任何 FrameworkElement（推荐 Border） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/animation.json'}"
        ani:AnimatableBitmap.AutoStart="True"
        ani:AnimatableBitmap.RepeatBehavior="Forever" />

<!-- GPU 加速（Lottie 默认开启） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/lottie.json', UseGPU=True}" />

<!-- 预加载全部帧画面（GIF/WebP/APNG 有效） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/cat.gif', Preload=True}" />

<!-- 进度条（双向绑定） -->
<Slider Maximum="{Binding ElementName=img, Path=(ani:AnimatableBitmap.Source).Metadata.Duration, Mode=OneWay}"
        Value="{Binding ElementName=img, Path=(ani:AnimatableBitmap.AnimationTime), Mode=TwoWay}" />

<!-- 命令绑定 -->
<StackPanel DataContext="{Binding ElementName=img, Path=(ani:AnimatableBitmap.Source)}"
            Orientation="Horizontal">
    <Button Command="{Binding BeginCommand, Mode=OneTime}" Content="Play" />
    <Button Command="{Binding PauseCommand, Mode=OneTime}" Content="Pause" />
    <Button Command="{Binding StopCommand, Mode=OneTime}" Content="Stop" />
</StackPanel>

<!-- Stretch 缩放模式 -->
<Border ani:AnimatableBitmap.Stretch="Uniform" />
```

**代码创建：**

```csharp
var options = new AnimatableBitmapOptions(filePath, useGPU: true, preload: false);
var bitmap = AnimatableBitmapFactory.Default.Create(options);
AnimatableBitmap.SetSource(hostElement, bitmap);
```

**修改默认配置：**

```csharp
AnimatableBitmapOptions.Default = new AnimatableBitmapOptions()
{
    UseGPU = true,    // Lottie 显卡加速（默认开启）
    Preload = false,  // 预加载缓存
};
```

## 📋 附加属性一览

| 属性 | 类型 | 说明 |
|------|------|------|
| `Source` | `AnimatableBitmap` | 动图数据模型 |
| `AnimationTime` | `double` | 当前播放时间（毫秒），支持双向绑定 |
| `AutoStart` | `bool` | 是否自动播放（默认 `True`） |
| `RepeatBehavior` | `RepeatBehavior?` | 循环行为（`Forever` / 指定次数） |
| `Stretch` | `Stretch` | 拉伸模式（`Uniform`/`Fill`/`UniformToFill`/`None`） |

## ⚙️ 系统要求

- Windows 10 1809 (Build 17763) 及以上
- .NET 8.0
- WinUI 3 / Windows App SDK

## ✈️ 更新日志

v3.0.0  
🚀 首个 WinUI 版本发布。基于 D3D12 + Composition API 的全新 GPU 加速管线。
