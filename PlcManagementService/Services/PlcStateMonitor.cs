
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace PLCService.Services
{
    /// <summary>
    /// PLC状态监控器（替代原来在代码中注释掉的部分）
    /// </summary>
    public static class PlcStateMonitor
    {
        private static readonly ConcurrentDictionary<string, PlcStateInfo> _plcStates =
            new ConcurrentDictionary<string, PlcStateInfo>();

        /// <summary>
        /// 检查PLC是否应该使用快速刷新
        /// </summary>
        public static bool ShouldUseFastRefresh(string plcId, int operationResult, bool isConnected)
        {
            if (!isConnected)
            {
                return false; // 连接不正常，不需要快速刷新
            }

            // 空闲或暂停状态不需要快速刷新
            bool isIdleOrPaused = (operationResult == 0 || operationResult == 11);

            // 更新状态记录
            var stateInfo = _plcStates.GetOrAdd(plcId, id => new PlcStateInfo());
            stateInfo.LastOperationResult = operationResult;
            stateInfo.LastUpdateTime = DateTime.Now;
            stateInfo.IsConnected = isConnected;
            stateInfo.RequiresFastRefresh = !isIdleOrPaused;

            return !isIdleOrPaused;
        }

        /// <summary>
        /// 获取PLC状态信息
        /// </summary>
        public static PlcStateInfo GetPlcState(string plcId)
        {
            return _plcStates.TryGetValue(plcId, out var state) ? state : null;
        }

        /// <summary>
        /// 清理不活跃的PLC状态
        /// </summary>
        public static void CleanupInactiveStates(TimeSpan maxInactiveTime)
        {
            var cutoffTime = DateTime.Now - maxInactiveTime;
            var toRemove = new List<string>();

            foreach (var kvp in _plcStates)
            {
                if (kvp.Value.LastUpdateTime < cutoffTime)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var plcId in toRemove)
            {
                _plcStates.TryRemove(plcId, out _);
            }
        }
    }

    /// <summary>
    /// PLC状态信息
    /// </summary>
    public class PlcStateInfo
    {
        public int LastOperationResult { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public bool IsConnected { get; set; }
        public bool RequiresFastRefresh { get; set; }

        public override string ToString()
        {
            return $"操作结果:{LastOperationResult}, 连接:{IsConnected}, 快速刷新:{RequiresFastRefresh}, 更新时间:{LastUpdateTime:yyyy-MM-dd HH:mm:ss}";
        }
    }
}
