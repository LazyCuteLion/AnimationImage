using System.Collections.Generic;

namespace AnimationImage
{
    /// <summary>
    /// 帧时间轴工具：将毫秒时间映射为帧索引。
    /// 先尝试基于当前帧的局部判断（n-1, n, n+1），若不命中则使用二分查找兜底。
    /// </summary>
    internal static class FrameTimeHelper
    {
        /// <summary>
        /// 将时间（毫秒）映射为帧索引。
        /// </summary>
        /// <param name="milliseconds">当前时间点（毫秒）</param>
        /// <param name="durations">累计时间轴列表（每帧的结束时间点）</param>
        /// <param name="currentIndex">当前已呈现的帧索引</param>
        public static int TimeToIndex(double milliseconds, List<double> durations, int currentIndex)
        {
            if (milliseconds == 0 || durations.Count <= 1)
                return 0;

            // 快速判断邻近帧，减少二分查找开销
            var index = currentIndex > -1 ? currentIndex : 0;
            if (index >= durations.Count)
                index %= durations.Count;

            if (milliseconds < durations[index])
            {
                if (index == 0)
                    return 0;
                if (index > 0 && milliseconds >= durations[index - 1])
                    return index;
                if (index > 1 && milliseconds >= durations[index - 2] && milliseconds < durations[index - 1])
                    return index - 1;
            }
            else if (index < durations.Count - 1 && milliseconds < durations[index + 1])
            {
                return index + 1;
            }

            // 二分查找第一个 >= milliseconds 的位置
            index = durations.BinarySearch(milliseconds);
            if (index < 0)
                index = ~index;
            else
                index++; // 精确匹配时，取下一个帧

            if (index >= durations.Count)
                index = 0;

            return index;
        }
    }
}
