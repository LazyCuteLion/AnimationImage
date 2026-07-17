# AnimationImage

> **基于 SkiaSharp 的 WPF / WinUI / Avalonia 极简动图播放方案**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## 🚀 简介

**AnimationImage** 支持播放 **Lottie(JSON)、GIF、WebP、APNG** 格式，相比现有方案，具有更高的帧率、更佳的渲染性能以及更低的内存占用。

### ✨ 核心特性

*   **三平台**：一套核心代码，WPF、WinUI 3、Avalonia 三平台原生体验。
*   **多格式**：**Lottie、GIF、WebP、APNG** 一站式支持。
*   **高帧率**：框架 Animation 引擎驱动，告别定时器抖动；WPF 可自定义帧率，突破默认 60FPS 至显示器刷新率。
*   **零侵入**：附加属性或标记扩展，原生控件即为渲染器，无需自定义控件。
*   **全可控**：自动播放、循环次数、进度跳转、`Play/Pause/Stop` 命令，开箱即用。

---

### 🎯 Lottie 矢量动画 — GPU 加速

Lottie 矢量动画的渲染开销远高于位图动图（每帧需实时光栅化复杂路径/渐变/遮罩），本方案通过 **D3D12 + Skia GPU 管线**显著降低 CPU 负载，GPU 可用时零 CPU 光栅化开销，即使复杂 Lottie 也能 144fps 丝滑运行。

**WinUI播放1080x700**

| 层级 | 路径 | 渲染耗时 |
|------|------|------|
| Tier 1 | D3D12 → Skia GPU → D3D11On12 → Win2D → Composition（零拷贝） | ~6-8ms |
| Tier 2 | D3D12 → Skia GPU → ReadPixels → CPU Present（自动降级） | ~11ms |
| CPU | Skia CPU 光栅化（GPU 完全不可用时） | ~15-20ms |

**WPF / Avalonia：** D3D12 + Skia GPU 光栅化，GPU 不可用时自动回退 CPU 光栅化。

### 📐 大分辨率动图 — MMF 智能缓存

对于 1920×1080以上的大分辨率 GIF/WebP/APNG，传统方案面临两难：
- 实时解码：CPU 解码跟不上帧率，播放卡顿掉帧
- 全量预加载到内存：1080p × 200帧 ≈ 1.5GB 内存，直接 OOM

本方案通过 **MMF（内存映射文件）帧缓存** 实现极度流畅播放：
- 后台线程逐帧解码并写入磁盘映射文件
- 播放时直接从 MMF 读取像素（OS 页面缓存管理，无需手动 GC）
- 跨实例复用：同一文件多处引用共享同一份缓存

效果：大分辨率高帧率动图播放流畅且内存占用极低，并可以随意跳转进度。

---

性能表现：  
1、Lottie动画  
![wpf-lottie](Images/wpf-lottie.webp)  
![avalonia-lottie](Images/avalonia-lottie.webp)  

2、GIF 800x600 50FPS  
![wpf-gif-800x600-50fps](Images/wpf-gif-800x600-50fps.webp)  
![avalonia-gif-800x600-50fps](Images/avalonia-gif-800x600-50fps.webp)  

## 📦 安装

通过 NuGet 包管理器安装：

```bash
# WPF 版本
Install-Package AnimationImage.WPF

# WinUI 3 版本
Install-Package AnimationImage.WinUI

# Avalonia 版本
Install-Package AnimationImage.Avalonia
```

---

## 使用方法

### WPF / Avalonia

引入命名空间：`xmlns:ani="https://github.com/LazyCuteLion/AnimationImage"`  

```xaml
<!-- 指定帧率为144，永久循环 -->
<Image ani:AnimatableBitmap.Source="[path]"
       ani:AnimatableBitmap.ForceFPS="144"
       ani:AnimatableBitmap.RepeatBehavior="Forever" />

<!-- 预加载全部帧画面（gif/webp/apng有效） -->
<Image Source="{ani:AnimatableBitmap '[path]',Preload=true}" />

<!-- GPU加速（Lottie有效） -->
<Image Source="{ani:AnimatableBitmap '[path]',UseGPU=true}" />

<!-- 也可以用到拥有Brush类型属性的控件 -->
<Rectangle Fill="{ani:AnimatableBitmap '[path]'}" />
<Border Background="{ani:AnimatableBitmap '[path]'}" />

<!-- 取消自动播放 -->
<Image ani:AnimatableBitmap.AutoStart="False" />

<!-- 进度条 -->
<Slider Maximum="{Binding ElementName=img, Path=(ani:AnimatableBitmap.Source).Metadata.Duration}"
        Value="{Binding ElementName=img, Path=(ani:AnimatableBitmap.AnimationTime), Mode=TwoWay}" />

<!-- 命令绑定 -->
<StackPanel DataContext="{Binding ElementName=img, Path=(ani:AnimatableBitmap.Source)}"
            Orientation="Horizontal">
    <Button Command="{Binding BeginCommand, Mode=OneTime}" Content="Play" />
    <Button Command="{Binding PauseCommand, Mode=OneTime}" Content="Pause" />
    <Button Command="{Binding StopCommand, Mode=OneTime}" Content="Stop" />
</StackPanel>
```

### WinUI （用法几乎一致）

引入命名空间：`xmlns:ani="using:AnimationImage"`

```xaml
<!-- 宿主元素可以是任何 FrameworkElement（推荐 Border） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/animation.json'}"
        ani:AnimatableBitmap.AutoStart="True"
        ani:AnimatableBitmap.RepeatBehavior="Forever" />

<!-- GPU加速（Lottie默认开启） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/lottie.json', UseGPU=True}" />

<!-- 预加载（GIF/WebP/APNG有效） -->
<Border ani:AnimatableBitmap.Source="{ani:AnimatableBitmap Source='ms-appx:///Assets/cat.gif', Preload=True}" />
```
