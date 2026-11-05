using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>独立采集汽车通行事实，不依赖路况横断面的绘制条件。</summary>
    internal sealed class CarNavigationExport
    {
        private bool? Forward;
        private bool? Backward;
        private float? ForwardSpeed;
        private float? BackwardSpeed;
        private bool m_ForwardSpeedIncomplete;
        private bool m_BackwardSpeedIncomplete;

        internal static CarNavigationExport Capture(EntityManager em, Entity road, CmmIssueCollector issues)
        {
            var facts = new CarNavigationExport();
            Entity[] lanes;
            try
            {
                if (!em.HasBuffer<Game.Net.SubLane>(road))
                    return facts.Complete(road.Index, issues);
                var buffer = em.GetBuffer<Game.Net.SubLane>(road, true);
                lanes = new Entity[buffer.Length];
                for (var i = 0; i < buffer.Length; i++) lanes[i] = buffer[i].m_SubLane;
            }
            catch (Exception error) when (error is ArgumentException
                || error is InvalidOperationException && !(error is ObjectDisposedException))
            {
                issues.Warn("CAR_DIRECTION_UNAVAILABLE", "Segment " + road.Index
                    + " lane list omitted: " + error.Message);
                return facts.Complete(road.Index, issues);
            }

            if (lanes.Length == 0) return facts.Complete(road.Index, issues);
            facts.Forward = facts.Backward = false;
            var seen = new HashSet<Entity>();
            foreach (var lane in lanes)
            {
                if (!seen.Add(lane)) continue;
                bool forward, backward;
                try
                {
                    // NodeLane 是路口或出入口连接，不参与路段方向与限速汇总。
                    if (em.HasComponent<Game.Net.MasterLane>(lane)
                        || em.HasComponent<Game.Net.SecondaryLane>(lane)
                        || em.HasComponent<Game.Net.NodeLane>(lane)
                        || em.HasComponent<Game.Common.Deleted>(lane)
                        || em.HasComponent<Game.Tools.Temp>(lane)
                        || !em.HasComponent<Game.Net.CarLane>(lane)) continue;
                    var car = em.GetComponentData<Game.Net.CarLane>(lane);
                    if ((car.m_Flags & (Game.Net.CarLaneFlags.Runway | Game.Net.CarLaneFlags.Forbidden
                        | Game.Net.CarLaneFlags.PublicOnly)) != 0) continue;
                    if (!em.HasComponent<Game.Prefabs.PrefabRef>(lane)
                        || !em.HasComponent<Game.Net.EdgeLane>(lane))
                        throw new InvalidOperationException("Missing car lane prefab or edge direction");
                    var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab;
                    if (!em.HasComponent<Game.Prefabs.CarLaneData>(prefab))
                        throw new InvalidOperationException("Missing car lane road types");
                    if ((em.GetComponentData<Game.Prefabs.CarLaneData>(prefab).m_RoadTypes
                        & Game.Net.RoadTypes.Car) == 0) continue;
                    var delta = em.GetComponentData<Game.Net.EdgeLane>(lane).m_EdgeDelta;
                    if (!math.all(math.isfinite(delta)))
                        throw new InvalidOperationException("Invalid car lane edge direction");
                    if (math.cmin(delta) > 0.5f || math.cmax(delta) <= 0.5f) continue;
                    forward = (car.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0 || delta.y > delta.x;
                    backward = (car.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0 || delta.y < delta.x;
                }
                catch (Exception error) when (error is ArgumentException
                    || error is InvalidOperationException && !(error is ObjectDisposedException))
                {
                    facts.MarkDirectionUnknown();
                    issues.Warn("CAR_DIRECTION_UNAVAILABLE", "Segment " + road.Index + ", lane "
                        + lane.Index + " direction omitted: " + error.Message);
                    continue;
                }

                if (forward) facts.Forward = true;
                if (backward) facts.Backward = true;
                facts.CaptureSpeed(em, lane, forward, backward, road.Index, issues);
            }
            return facts.Complete(road.Index, issues);
        }

        private void MarkDirectionUnknown()
        {
            if (Forward != true) Forward = null;
            if (Backward != true) Backward = null;
            // 未知方向的车道可能提高任一方向的有效限速，不能发布不完整的最大值。
            ForwardSpeed = BackwardSpeed = null;
            m_ForwardSpeedIncomplete = m_BackwardSpeedIncomplete = true;
        }

        private void CaptureSpeed(EntityManager em, Entity lane, bool forward, bool backward,
            int roadId, CmmIssueCollector issues)
        {
            float speed;
            try
            {
                speed = em.GetComponentData<Game.Net.CarLane>(lane).m_SpeedLimit;
            }
            catch (Exception error) when (error is ArgumentException
                || error is InvalidOperationException && !(error is ObjectDisposedException))
            {
                OmitSpeed(forward, backward);
                issues.Warn("CAR_SPEED_UNAVAILABLE", "Segment " + roadId + ", lane " + lane.Index
                    + " speed omitted: " + error.Message);
                return;
            }
            if (!TrafficSnapshotMath.Finite(speed) || speed <= 0)
            {
                OmitSpeed(forward, backward);
                issues.Warn("CAR_SPEED_UNAVAILABLE", "Segment " + roadId + ", lane " + lane.Index
                    + " speed omitted: missing or invalid speed limit");
                return;
            }
            if (forward && !m_ForwardSpeedIncomplete) ForwardSpeed = Math.Max(ForwardSpeed ?? 0, speed);
            if (backward && !m_BackwardSpeedIncomplete) BackwardSpeed = Math.Max(BackwardSpeed ?? 0, speed);
        }

        private void OmitSpeed(bool forward, bool backward)
        {
            if (forward) { ForwardSpeed = null; m_ForwardSpeedIncomplete = true; }
            if (backward) { BackwardSpeed = null; m_BackwardSpeedIncomplete = true; }
        }

        private CarNavigationExport Complete(int roadId, CmmIssueCollector issues)
        {
            if (!Forward.HasValue || !Backward.HasValue)
                issues.Warn("CAR_DIRECTION_ASSUMED", "Segment " + roadId
                    + " missing car direction; unknown directions allowed.");
            if (!Forward.HasValue && !m_ForwardSpeedIncomplete
                || !Backward.HasValue && !m_BackwardSpeedIncomplete)
                issues.Warn("CAR_SPEED_UNAVAILABLE", "Segment " + roadId
                    + " speed omitted for assumed car direction.");
            return this;
        }

        internal string AccessJson() => new JObject {
            ["forward"] = (Forward ?? true) ? "allowed" : "forbidden",
            ["backward"] = (Backward ?? true) ? "allowed" : "forbidden",
            ["source"] = !Forward.HasValue || !Backward.HasValue
                ? "cs2-default-allow-v1" : "cs2-native-lanes-v1"
        }.ToString(Formatting.None);

        internal string SpeedJson() => new JObject {
            ["forward"] = ForwardSpeed.HasValue ? new JValue(ForwardSpeed.Value) : JValue.CreateNull(),
            ["backward"] = BackwardSpeed.HasValue ? new JValue(BackwardSpeed.Value) : JValue.CreateNull(),
            ["unit"] = "m/s", ["source"] = "cs2-effective-car-lanes-v1"
        }.ToString(Formatting.None);
    }
}
