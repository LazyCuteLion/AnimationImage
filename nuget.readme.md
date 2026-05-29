# AnimationImage

> **基于 SkiaSharp 的 WPF & AvaloniaUI 极简动图播放方案**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![GitHub Repo](https://img.shields.io/badge/GitHub-Repo-blue?logo=github)](https://github.com/LazyCuteLion/AnimationImage)


## 🚀 简介

**AnimationImage**支持播放 **Lottie(JSON)**、**GIF** 和 **WebP** 格式，相比现有方案，具有更高的帧率、更佳的渲染性能以及更低的内存占用。

## 🚝[使用方法](https://github.com/LazyCuteLion/AnimationImage)  

WPF：`xmlns:ani="clr-namespace:AnimationImage.WPF;assembly=AnimationImage.WPF"`  

```xaml
<!-- 指定帧率为144，永久循环 -->
<Image ani:AnimationBehavior.AnimatableBitmap="[path]"
       ani:AnimationBehavior.ForceFPS="144"
       ani:AnimationBehavior.RepeatBehavior="Forever" />

<!-- 全量缓存（gif/webp有效） -->
<Image Source="{ani:AnimatableBitmap '[path]',PreloadCount=PreloadOptions.Full}" />

<!-- 设置渲染比例（Lottie有效） -->
<Image Source="{ani:AnimatableBitmap '[path]',RenderScale=0.5}" />

<!-- 也可以用到拥有Brush类型属性的控件 -->
<Rectangle Fill="{ani:AnimatableBitmap '[path]'}" />

<Border Background="{ani:AnimatableBitmap '[path]'}" />

<!-- 取消自动播放 -->
<Image ani:AnimationBehavior.AutoStart="false" …… />

<!-- 进度条 -->
<Slider Maximum="{Binding ElementName=img, Path=(ani:AnimationBehavior.AnimatableBitmap).Metadata.Duration}"
        Value="{Binding ElementName=img, Path=(ani:AnimationBehavior.AnimationTime), Mode=TwoWay}" />

<!-- 命令绑定 -->
<StackPanel DataContext="{Binding ElementName=img, Path=(ani:AnimationBehavior.AnimatableBitmap)}"
            Orientation="Horizontal">
            <Button Command="{Binding BeginCommand, Mode=OneTime}"
                    Content="Play" />
            <Button Margin="10,0"
                    Command="{Binding PauseCommand, Mode=OneTime}"
                    Content="Pause" />
            <Button Command="{Binding StopCommand, Mode=OneTime}"
                    Content="Stop" />
</StackPanel>
```

Avalonia（用法与WPF基本相同）：`xmlns:ani="using:AnimationImage.Avalonia"` 
```axaml
<!-- 永久循环 -->
<Image ani:AnimationBehavior.AnimatableBitmap="[path]"
       ani:AnimationBehavior.LoopCount="-1" />
```

```C#
public partial class App : Application
{
   public override void Initialize()
   {
       AnimatableBitmapOptions.Default = new AnimatableBitmapOptions()
       {
           UseGPU = false,//禁用显卡加速
           PreloadCount = PreloadOptions.Disable,//禁用预加载和缓存
       };
       AvaloniaXamlLoader.Load(this);
   }
}
```

## ✈️更新日志
v1.0.6  
🧨 功能回归：全新的`GIF/WebP 预加载机制`：若解码速度不能满足帧率要求，则自动预加载，然后开启后台线程持续解码并缓存。可通过`AnimatableBitmapOptions.Default`来调整这一默认行为。  

v1.0.5  
👏 重大变更：引入`Vortice.Direct3D12`，为Lottie提供显卡加速功能，对于复杂场景的动画有很大的提升！默认已开启，也可以通过`UseGPU`关闭。 

v1.0.4  
🐛 修复：修复Lottie文件不能自动播放的问题。

v1.0.3  
✨ 优化：对于Lottie文件，增加了设置渲染比例选项。  
🚨 重大变更：预加载功能有较大缺陷，暂时取消。

v1.0.2  
✨ 优化：AI生成的代码不是很靠谱，重新优化预加载算法。

v1.0.1  
🐛 修复：修复命令状态没有即时更新的问题。

v1.0.0  
🚀 发布：初始版本正式发布。