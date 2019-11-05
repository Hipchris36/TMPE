using TrafficManager.Manager.Impl;
using ColossalFramework;
using CSUtil.Commons;
using TrafficManager.API.Manager;
using TrafficManager.API.Traffic.Data;

namespace TrafficManager.Util {
    public static class DirectionUtil {
        /// <summary>
        /// returns the number of all target lanes from input segment toward the secified direction.
        /// </summary>
        public static int CountTargetLanesTowardDirection(ushort segmentId, ushort nodeId, ArrowDirection dir) {
            int count = 0;

            LaneArrowManager.Instance.Services.NetService.IterateNodeSegments(
                nodeId,
                (ushort otherSegmentId, ref NetSegment otherSeg) => {
                    ArrowDirection dir2 = GetDirection(segmentId, otherSegmentId, nodeId);
                    if (dir == dir2) {
                        int forward = 0, backward = 0;
                        otherSeg.CountLanes(
                            otherSegmentId,
                            NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle,
                            VehicleInfo.VehicleType.Car,
                            ref forward,
                            ref backward);

                        bool startNode2 = otherSeg.m_startNode == nodeId;
                        bool invert2 = (NetManager.instance.m_segments.m_buffer[segmentId].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
                            //xor because inverting 2 times is redundant.
                        if (invert2 ^ (!startNode2)) {
                            count += backward;
                        } else {
                            count += forward;
                        }
                    }
                    return true;
                });

            return count;
        }

        public static ArrowDirection GetDirection(ushort segment1Id, ushort segment2Id, ushort nodeId) {
            ref NetSegment segment1 = ref Singleton<NetManager>.instance.m_segments.m_buffer[segment1Id];
            bool startNode = segment1.m_startNode == nodeId;
            IExtSegmentEndManager segEndMan = Constants.ManagerFactory.ExtSegmentEndManager;
            ExtSegmentEnd segEnd = segEndMan.ExtSegmentEnds[segEndMan.GetIndex(segment1Id, startNode)];
            return segEndMan.GetDirection(ref segEnd, segment2Id);
        }

        public static bool IsOneWay(ushort segmentId) {
            NetSegment seg = Singleton<NetManager>.instance.m_segments.m_buffer[segmentId];
            int forward = 0, backward = 0;
            seg.CountLanes(
                    segmentId,
                    NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle,
                    VehicleInfo.VehicleType.Car,
                    ref forward,
                    ref backward);
            return forward == 0 || backward == 0;
        }
    }
}
