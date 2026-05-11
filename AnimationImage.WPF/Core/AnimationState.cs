using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimationImage.Core
{
    public enum AnimationState
    {
        /// <summary>
        /// 发生错误（解码器初始化失败、帧解码错误等）
        /// </summary>
        Error = -1,
        
        /// <summary>
        /// 初始状态
        /// </summary>
        None = 0,

        /// <summary>
        /// 已停止（主动结束播放）
        /// 和自然播放结束不同，会定位到起点
        /// </summary>
        Stopped = 1,

        /// <summary>
        /// 正在播放，动画时间线正在前进
        /// </summary>
        Playing = 2,

        /// <summary>
        /// 已暂停，动画保持在当前帧，等待恢复
        /// </summary>
        Paused = 3,

        /// <summary>
        /// 已完成，动画自然播放结束（到达 Duration 终点）
        /// 和主动停止不同，会停留在终点
        /// </summary>
        Completed = 4
    }
}
