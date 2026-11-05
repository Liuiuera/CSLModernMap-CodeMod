using System;
using ColossalFramework;
using TrafficManager.API.Manager;
using TrafficManager.API.Traffic.Data;
using TrafficManager.Manager.Impl;

namespace CSLModernMap.Traffic
{
    /// <summary>
    /// 读取 TM:PE 的路段信息和车道交通统计。
    /// </summary>
    public static class TMPEBridge
    {
        /// <summary>LaneTrafficData.meanSpeed 的 UInt16 标度：10000 = 相对速度 1.0。</summary>
        public const float RELATIVE_SPEED_SCALE = 10000f;

        private static readonly ExtSegment[] _extSegArray;
        private static readonly ITrafficMeasurementManager _trafficMeasurementMgr;

        public static bool IsAvailable { get; private set; }
        public static string StatusString { get; private set; }

        static TMPEBridge()
        {
            try
            {
                ExtSegmentManager segmentManager = ExtSegmentManager.Instance;
                if (segmentManager == null) { StatusString = "ExtSegmentManager.Instance is null."; IsAvailable = false; return; }
                _extSegArray = segmentManager.ExtSegments;

                IManagerFactory factory = TrafficManager.Constants.ManagerFactory;
                if (factory == null) { StatusString = "TM:PE Constants.ManagerFactory is null."; IsAvailable = false; return; }
                _trafficMeasurementMgr = factory.TrafficMeasurementManager;
                if (_trafficMeasurementMgr == null) { StatusString = "ManagerFactory.TrafficMeasurementManager is null."; IsAvailable = false; return; }

                int arrLen = _extSegArray != null ? _extSegArray.Length : 0;
                IsAvailable = arrLen > 1000;
                StatusString = "OK Strong-ref TMPE v11 STABLE. GetLaneTrafficData-mode. ExtSegmentArray.Length=" + arrLen;
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                StatusString = "TMPEBridge ctor ex: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>读取 ExtSegment 标志位 + TMPE 维护的 LaneId 数组（uint[]，长度 = TMPE 监控的车道数）。</summary>
        public static bool TryGetSegmentInfo(ushort segmentId, out uint[] tmpeLaneIds, out bool oneWay, out bool highway, out bool buslane)
        {
            tmpeLaneIds = null; oneWay = false; highway = false; buslane = false;
            if (!IsAvailable) return false;
            try
            {
                if (segmentId >= _extSegArray.Length) return false;
                ExtSegment s = _extSegArray[segmentId];
                if (!s.valid) return false;
                tmpeLaneIds = s.lanes;
                oneWay = s.oneWay;
                highway = s.highway;
                buslane = s.buslane;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 从 TMPE ITrafficMeasurementManager 一次性读取一条车道的两项核心统计：
        ///   • relativeSpeed01 = LaneTrafficData.meanSpeed / 10000f，范围 [0,1]
        ///   • vehicleCount    = LaneTrafficData.maxTrafficBuffer，滑动窗口缓存内车辆数近似
        ///
        /// 返回值：
        ///   true  = TMPE 成功返回（滑动窗口已积累足够统计）
        ///   false = TMPE 返回 false（车道刚创建或没有车经过，滑窗无统计）
        /// </summary>
        public static bool TryGetLaneSnapshot(ushort segmentId, int laneIndex,
                                              out float relativeSpeed01, out int vehicleCount)
        {
            relativeSpeed01 = 0f;
            vehicleCount = 0;
            if (!IsAvailable) return false;
            if (laneIndex < 0 || laneIndex > 255) return false;
            try
            {
                LaneTrafficData data;
                bool ok = _trafficMeasurementMgr.GetLaneTrafficData(segmentId, (byte)laneIndex, out data);
                if (!ok) return false;

                // meanSpeed: UInt16, scale 0 ~ 10000 (10000 = 100% speed limit)
                // Clamp 防 TMPE 返回异常值
                ushort rawMean = data.meanSpeed;
                float rawF = (rawMean > 10000) ? 10000f : (float)rawMean;
                relativeSpeed01 = rawF / RELATIVE_SPEED_SCALE;

                // maxTrafficBuffer: UInt16 -> clamp to Int32 reasonable range
                vehicleCount = (int)data.maxTrafficBuffer;
                if (vehicleCount > 1024) vehicleCount = 1024;  // 单车道不可能超过 1024 辆，防御异常

                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
