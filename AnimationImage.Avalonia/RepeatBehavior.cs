using System;
using System.ComponentModel;
using System.Globalization;

namespace AnimationImage
{
    /// <summary>
    /// 对齐 WPF System.Windows.Media.Animation.RepeatBehavior 的跨平台兼容类型。
    /// 支持 Forever、按次数、按时长三种循环模式。
    /// </summary>
    [TypeConverter(typeof(RepeatBehaviorConverter))]
    public struct RepeatBehavior
    {
        private enum RepeatBehaviorType
        {
            Count,
            Duration,
            Forever
        }

        private RepeatBehaviorType _type;
        private readonly double _count;
        private readonly TimeSpan _duration;

        /// <summary>
        /// 按次数循环
        /// </summary>
        public RepeatBehavior(double count)
        {
            _type = RepeatBehaviorType.Count;
            _count = count;
            _duration = TimeSpan.Zero;
        }

        /// <summary>
        /// 按时长循环
        /// </summary>
        public RepeatBehavior(TimeSpan duration)
        {
            _type = RepeatBehaviorType.Duration;
            _count = 0;
            _duration = duration;
        }

        /// <summary>
        /// 无限循环
        /// </summary>
        public static RepeatBehavior Forever
        {
            get
            {
                var behavior = new RepeatBehavior();
                behavior._type = RepeatBehaviorType.Forever;
                return behavior;
            }
        }

        public bool IsForever => _type == RepeatBehaviorType.Forever;
        public double Count => _count;
        public TimeSpan Duration => _duration;
        public bool HasCount => _type == RepeatBehaviorType.Count;
        public bool HasDuration => _type == RepeatBehaviorType.Duration;

        public override string ToString()
        {
            if (IsForever) return "Forever";
            if (HasCount) return $"{_count}x";
            return _duration.ToString();
        }

        /// <summary>
        /// 从字符串解析 RepeatBehavior，支持 "Forever"、"3x"、时长格式
        /// </summary>
        public static RepeatBehavior Parse(string s)
        {
            if (s == "Forever") return Forever;
            if (s.EndsWith("x") && double.TryParse(s[..^1], out var c))
                return new RepeatBehavior(c);
            if (TimeSpan.TryParse(s, out var d))
                return new RepeatBehavior(d);
            return new RepeatBehavior(1);
        }
    }

    /// <summary>
    /// RepeatBehavior 的 XAML 类型转换器
    /// </summary>
    public class RepeatBehaviorConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => value is string s ? RepeatBehavior.Parse(s) : base.ConvertFrom(context, culture, value);
    }
}
