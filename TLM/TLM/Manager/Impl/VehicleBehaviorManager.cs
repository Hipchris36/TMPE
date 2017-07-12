﻿using ColossalFramework;
using ColossalFramework.Math;
using CSUtil.Commons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrafficManager.Custom.AI;
using TrafficManager.Custom.PathFinding;
using TrafficManager.Geometry.Impl;
using TrafficManager.State;
using TrafficManager.Traffic;
using TrafficManager.Traffic.Data;
using TrafficManager.UI;
using TrafficManager.Util;
using UnityEngine;
using static TrafficManager.Traffic.Data.PrioritySegment;

namespace TrafficManager.Manager.Impl {
	public class VehicleBehaviorManager : AbstractCustomManager, IVehicleBehaviorManager {
		public const float MIN_SPEED = 8f * 0.2f; // 10 km/h
		public const float ICY_ROADS_MIN_SPEED = 8f * 0.4f; // 20 km/h
		public const float ICY_ROADS_STUDDED_MIN_SPEED = 8f * 0.8f; // 40 km/h
		public const float WET_ROADS_MAX_SPEED = 8f * 2f; // 100 km/h
		public const float WET_ROADS_FACTOR = 0.75f;
		public const float BROKEN_ROADS_MAX_SPEED = 8f * 1.6f; // 80 km/h
		public const float BROKEN_ROADS_FACTOR = 0.75f;

		private static PathUnit.Position DUMMY_POS = default(PathUnit.Position);

		public static readonly VehicleBehaviorManager Instance = new VehicleBehaviorManager();

		private VehicleBehaviorManager() {

		}

		public bool IsSpaceReservationAllowed(ushort transitNodeId, PathUnit.Position sourcePos, PathUnit.Position targetPos) {
			if (!Options.timedLightsEnabled) {
				return true;
			}

			if (TrafficLightSimulationManager.Instance.HasActiveTimedSimulation(transitNodeId)) {
				RoadBaseAI.TrafficLightState vehLightState;
				RoadBaseAI.TrafficLightState pedLightState;
#if DEBUG
				Vehicle dummyVeh = default(Vehicle);
#endif
				CustomRoadAI.GetTrafficLightState(
#if DEBUG
					0, ref dummyVeh,
#endif
					transitNodeId, sourcePos.m_segment, sourcePos.m_lane, targetPos.m_segment, ref Singleton<NetManager>.instance.m_segments.m_buffer[sourcePos.m_segment], 0, out vehLightState, out pedLightState);

				if (vehLightState == RoadBaseAI.TrafficLightState.Red) {
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Checks for traffic lights and priority signs when changing segments (for rail vehicles).
		/// Sets the maximum allowed speed <paramref name="maxSpeed"/> if segment change is not allowed (otherwise <paramref name="maxSpeed"/> has to be set by the calling method).
		/// </summary>
		/// <param name="frontVehicleId">vehicle id</param>
		/// <param name="vehicleData">vehicle data</param>
		/// <param name="lastFrameData">last frame data of vehicle</param>
		/// <param name="isRecklessDriver">if true, this vehicle ignores red traffic lights and priority signs</param>
		/// <param name="prevPos">previous path position</param>
		/// <param name="prevTargetNodeId">previous target node</param>
		/// <param name="prevLaneID">previous lane</param>
		/// <param name="position">current path position</param>
		/// <param name="targetNodeId">transit node</param>
		/// <param name="laneID">current lane</param>
		/// <param name="maxSpeed">maximum allowed speed (only valid if method returns false)</param>
		/// <returns>true, if the vehicle may change segments, false otherwise.</returns>
		public bool MayChangeSegment(ushort frontVehicleId, ref VehicleState vehicleState, ref Vehicle vehicleData, ref Vehicle.Frame lastFrameData, bool isRecklessDriver, ref PathUnit.Position prevPos, ref NetSegment prevSegment, ushort prevTargetNodeId, uint prevLaneID, ref PathUnit.Position position, ushort targetNodeId, ref NetNode targetNode, uint laneID, out float maxSpeed) {
			return MayChangeSegment(frontVehicleId, ref vehicleState, ref vehicleData, ref lastFrameData, isRecklessDriver, ref prevPos, ref prevSegment, prevTargetNodeId, prevLaneID, ref position, targetNodeId, ref targetNode, laneID, ref DUMMY_POS, 0, out maxSpeed);
		}

		/// <summary>
		/// Checks for traffic lights and priority signs when changing segments (for road & rail vehicles).
		/// Sets the maximum allowed speed <paramref name="maxSpeed"/> if segment change is not allowed (otherwise <paramref name="maxSpeed"/> has to be set by the calling method).
		/// </summary>
		/// <param name="frontVehicleId">vehicle id</param>
		/// <param name="vehicleData">vehicle data</param>
		/// <param name="lastFrameData">last frame data of vehicle</param>
		/// <param name="isRecklessDriver">if true, this vehicle ignores red traffic lights and priority signs</param>
		/// <param name="prevPos">previous path position</param>
		/// <param name="prevTargetNodeId">previous target node</param>
		/// <param name="prevLaneID">previous lane</param>
		/// <param name="position">current path position</param>
		/// <param name="targetNodeId">transit node</param>
		/// <param name="laneID">current lane</param>
		/// <param name="nextPosition">next path position</param>
		/// <param name="nextTargetNodeId">next target node</param>
		/// <param name="maxSpeed">maximum allowed speed (only valid if method returns false)</param>
		/// <returns>true, if the vehicle may change segments, false otherwise.</returns>
		public bool MayChangeSegment(ushort frontVehicleId, ref VehicleState vehicleState, ref Vehicle vehicleData, ref Vehicle.Frame lastFrameData, bool isRecklessDriver, ref PathUnit.Position prevPos, ref NetSegment prevSegment, ushort prevTargetNodeId, uint prevLaneID, ref PathUnit.Position position, ushort targetNodeId, ref NetNode targetNode, uint laneID, ref PathUnit.Position nextPosition, ushort nextTargetNodeId, out float maxSpeed) {
#if DEBUG
			bool debug = GlobalConfig.Instance.DebugSwitches[13] && (GlobalConfig.Instance.DebugNodeId <= 0 || targetNodeId == GlobalConfig.Instance.DebugNodeId);
#endif
			if (prevTargetNodeId != targetNodeId
				|| (!VehicleBehaviorManager.Instance.MayDespawn(ref vehicleData) && vehicleData.m_blockCounter == 255) // NON-STOCK CODE
			) {
				// method should only be called if targetNodeId == prevTargetNode
				vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
				maxSpeed = 0f;
				return true;
			}

			VehicleStateManager vehStateManager = VehicleStateManager.Instance;

			var netManager = Singleton<NetManager>.instance;

			uint currentFrameIndex = Singleton<SimulationManager>.instance.m_currentFrameIndex;
			uint prevTargetNodeLower8Bits = (uint)((prevTargetNodeId << 8) / 32768);
			uint random = currentFrameIndex - prevTargetNodeLower8Bits & 255u;

			bool isRailVehicle = (vehicleData.Info.m_vehicleType & (VehicleInfo.VehicleType.Train | VehicleInfo.VehicleType.Metro | VehicleInfo.VehicleType.Monorail)) != VehicleInfo.VehicleType.None;
			bool isMonorail = vehicleData.Info.m_vehicleType == VehicleInfo.VehicleType.Monorail;

			NetNode.Flags targetNodeFlags = targetNode.m_flags;
			bool hasActiveTimedSimulation = (Options.timedLightsEnabled && TrafficLightSimulationManager.Instance.HasActiveTimedSimulation(targetNodeId));
			bool hasTrafficLightFlag = (targetNodeFlags & NetNode.Flags.TrafficLights) != NetNode.Flags.None;
			bool hasTrafficLight = hasTrafficLightFlag || hasActiveTimedSimulation;
			if (hasActiveTimedSimulation && ! hasTrafficLightFlag) {
				TrafficLightManager.Instance.AddTrafficLight(targetNodeId, ref targetNode);
			}
			bool hasStockYieldSign = false;
			float sqrSpeed = lastFrameData.m_velocity.sqrMagnitude;
			bool checkTrafficLights = hasActiveTimedSimulation;
			bool isTargetStartNode = prevSegment.m_startNode == targetNodeId;
			if (!isRailVehicle) {
				// check if to check space

#if DEBUG
				if (debug)
					Log._Debug($"CustomVehicleAI.MayChangeSegment: Vehicle {frontVehicleId} is not a train.");
#endif

				var prevLaneFlags = (NetLane.Flags)netManager.m_lanes.m_buffer[prevLaneID].m_flags;
				var hasCrossing = (targetNodeFlags & NetNode.Flags.LevelCrossing) != NetNode.Flags.None;
				var isJoinedJunction = (prevLaneFlags & NetLane.Flags.JoinedJunction) != NetLane.Flags.None;
				hasStockYieldSign = (prevLaneFlags & (NetLane.Flags.YieldStart | NetLane.Flags.YieldEnd)) != NetLane.Flags.None && (targetNodeFlags & (NetNode.Flags.Junction | NetNode.Flags.TrafficLights | NetNode.Flags.OneWayIn)) == NetNode.Flags.Junction;
				bool checkSpace = !Flags.getEnterWhenBlockedAllowed(prevPos.m_segment, isTargetStartNode) && !isRecklessDriver;

				//TrafficLightSimulation nodeSim = TrafficLightSimulation.GetNodeSimulation(destinationNodeId);
				//if (timedNode != null && timedNode.vehiclesMayEnterBlockedJunctions) {
				//	checkSpace = false;
				//}

				// stock priority signs
				if (/*!Options.prioritySignsEnabled &&*/ hasStockYieldSign && sqrSpeed > 0.01f && (vehicleData.m_flags & Vehicle.Flags.Emergency2) == (Vehicle.Flags)0) {
					vehicleState.JunctionTransitState = VehicleJunctionTransitState.Stop;
					maxSpeed = 0f;
					return false;
				}

				if (checkSpace) {
					// check if there is enough space
					if ((targetNodeFlags & (NetNode.Flags.Junction | NetNode.Flags.OneWayOut | NetNode.Flags.OneWayIn)) == NetNode.Flags.Junction &&
						targetNode.CountSegments() != 2) {
						var len = vehicleData.CalculateTotalLength(frontVehicleId) + 2f;
						if (!netManager.m_lanes.m_buffer[laneID].CheckSpace(len)) {
							var sufficientSpace = false;
							if (nextPosition.m_segment != 0 && netManager.m_lanes.m_buffer[laneID].m_length < 30f) {
								NetNode.Flags nextTargetNodeFlags = netManager.m_nodes.m_buffer[nextTargetNodeId].m_flags;
								if ((nextTargetNodeFlags & (NetNode.Flags.Junction | NetNode.Flags.OneWayOut | NetNode.Flags.OneWayIn)) != NetNode.Flags.Junction ||
									netManager.m_nodes.m_buffer[nextTargetNodeId].CountSegments() == 2) {
									uint nextLaneId = PathManager.GetLaneID(nextPosition);
									if (nextLaneId != 0u) {
										sufficientSpace = netManager.m_lanes.m_buffer[nextLaneId].CheckSpace(len);
									}
								}
							}
							if (!sufficientSpace) {
								maxSpeed = 0f;
#if DEBUG
								if (debug)
									Log._Debug($"Vehicle {frontVehicleId}: Setting JunctionTransitState to BLOCKED");
#endif

								vehicleState.JunctionTransitState = VehicleJunctionTransitState.Blocked;
								return false;
							}
						}
					}
				}

				checkTrafficLights = checkTrafficLights || (!isJoinedJunction || hasCrossing);
			} else {
#if DEBUG
				if (debug)
					Log._Debug($"CustomVehicleAI.MayChangeSegment: Vehicle {frontVehicleId} is a train/monorail.");
#endif

				if (!isMonorail) {
					checkTrafficLights = true;
				}
			}

			if (vehicleState.JunctionTransitState == VehicleJunctionTransitState.Blocked) {
#if DEBUG
				if (debug)
					Log._Debug($"Vehicle {frontVehicleId}: Setting JunctionTransitState from BLOCKED to APPROACH");
#endif
				vehicleState.JunctionTransitState = VehicleJunctionTransitState.Approach;
			}

			if ((vehicleData.m_flags & Vehicle.Flags.Emergency2) == 0) {
				if (hasTrafficLight && checkTrafficLights) {
#if DEBUG
					if (debug)
						Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Node {targetNodeId} has a traffic light.");
#endif

					var destinationInfo = targetNode.Info;

//					if (vehicleState.JunctionTransitState == VehicleJunctionTransitState.None) {
//#if DEBUG
//						if (debug)
//							Log._Debug($"Vehicle {vehicleId}: Setting JunctionTransitState to ENTER (1)");
//#endif
//						vehicleState.JunctionTransitState = VehicleJunctionTransitState.Approach;
//					}

					RoadBaseAI.TrafficLightState vehicleLightState;
					RoadBaseAI.TrafficLightState pedestrianLightState;
					bool vehicles;
					bool pedestrians;
					CustomRoadAI.GetTrafficLightState(
#if DEBUG
						frontVehicleId, ref vehicleData,
#endif
						targetNodeId, prevPos.m_segment, prevPos.m_lane, position.m_segment, ref prevSegment, currentFrameIndex - prevTargetNodeLower8Bits, out vehicleLightState, out pedestrianLightState, out vehicles, out pedestrians);

					if (vehicleData.Info.m_vehicleType == VehicleInfo.VehicleType.Car && isRecklessDriver) { // TODO no reckless driving at railroad crossings
						vehicleLightState = RoadBaseAI.TrafficLightState.Green;
					}

#if DEBUG
					if (debug)
						Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle {frontVehicleId} has TL state {vehicleLightState} at node {targetNodeId}");
#endif

					if (!vehicles && random >= 196u) {
						vehicles = true;
						RoadBaseAI.SetTrafficLightState(targetNodeId, ref prevSegment, currentFrameIndex - prevTargetNodeLower8Bits, vehicleLightState, pedestrianLightState, vehicles, pedestrians);
					}

					var stopCar = false;
					switch (vehicleLightState) {
						case RoadBaseAI.TrafficLightState.RedToGreen:
							if (random < 60u) {
								stopCar = true;
							}
							break;
						case RoadBaseAI.TrafficLightState.Red:
							stopCar = true;
							break;
						case RoadBaseAI.TrafficLightState.GreenToRed:
							if (random >= 30u) {
								stopCar = true;
							}
							break;
					}

					/*if ((vehicleLightState == RoadBaseAI.TrafficLightState.Green || vehicleLightState == RoadBaseAI.TrafficLightState.RedToGreen) && !Flags.getEnterWhenBlockedAllowed(prevPos.m_segment, prevSegment.m_startNode == targetNodeId)) {
						var hasIncomingCars = TrafficPriority.HasIncomingVehiclesWithHigherPriority(vehicleId, targetNodeId);

						if (hasIncomingCars) {
							// green light but other cars are incoming and they have priority: stop
							stopCar = true;
						}
					}*/

					if (stopCar) {
#if DEBUG
						if (debug)
							Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to STOP");
#endif
						vehicleState.JunctionTransitState = VehicleJunctionTransitState.Stop;
						maxSpeed = 0f;
						vehicleData.m_blockCounter = 0;
						return false;
					} else {
#if DEBUG
						if (debug)
							Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to LEAVE ({vehicleLightState})");
#endif
						vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
					}
				} else if (!isMonorail && Options.prioritySignsEnabled) {
					TrafficPriorityManager prioMan = TrafficPriorityManager.Instance;

#if DEBUG
					//bool debug = destinationNodeId == 10864;
					//bool debug = destinationNodeId == 13531;
					//bool debug = false;// targetNodeId == 5027;
#endif
					//bool debug = false;
#if DEBUG
					if (debug)
						Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle is arriving @ seg. {prevPos.m_segment} ({position.m_segment}, {nextPosition.m_segment}), node {targetNodeId} which is not a traffic light.");
#endif

					var sign = prioMan.GetPrioritySign(prevPos.m_segment, isTargetStartNode);
					if (sign != PrioritySegment.PriorityType.None) {
#if DEBUG
						if (debug)
							Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle is arriving @ seg. {prevPos.m_segment} ({position.m_segment}, {nextPosition.m_segment}), node {targetNodeId} which is not a traffic light and is a priority segment.");
#endif

#if DEBUG
						if (debug)
							Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): JunctionTransitState={vehicleState.JunctionTransitState.ToString()}");
#endif

						if (vehicleState.JunctionTransitState == VehicleJunctionTransitState.None) {
#if DEBUG
							if (debug)
								Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to APPROACH (prio)");
#endif
							vehicleState.JunctionTransitState = VehicleJunctionTransitState.Approach;
						}

						if (vehicleState.JunctionTransitState != VehicleJunctionTransitState.Leave) {
							bool hasPriority;
							switch (sign) {
								case PriorityType.Stop:
#if DEBUG
									if (debug)
										Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): STOP sign. waittime={vehicleState.waitTime}, sqrSpeed={sqrSpeed}");
#endif

									maxSpeed = 0f;

									if (vehicleState.waitTime < GlobalConfig.Instance.MaxPriorityWaitTime) {
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to STOP (wait) waitTime={vehicleState.waitTime}");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Stop;

										if (sqrSpeed <= TrafficPriorityManager.MAX_SQR_STOP_VELOCITY) {
											vehicleState.waitTime++;

											//float minStopWaitTime = Singleton<SimulationManager>.instance.m_randomizer.UInt32(3);
											if (vehicleState.waitTime >= 2) {
												if (Options.simAccuracy >= 4) {
													vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
												} else {
													hasPriority = prioMan.HasPriority(frontVehicleId, ref vehicleData, ref prevPos, targetNodeId, isTargetStartNode, ref position, ref targetNode);
#if DEBUG
													if (debug)
														Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): hasPriority={hasPriority}");
#endif

													if (!hasPriority) {
														vehicleData.m_blockCounter = 0;
														return false;
													}
#if DEBUG
													if (debug)
														Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to LEAVE (min wait timeout)");
#endif
													vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
												}
											} else {
#if DEBUG
												if (debug)
													Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle must first come to a full stop in front of the stop sign.");
#endif
												return false;
											}
										} else {
#if DEBUG
											if (debug)
												Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle has come to a full stop.");
#endif
											vehicleState.waitTime = 0;
											return false;
										}
									} else {
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Max. wait time exceeded. Setting JunctionTransitState to LEAVE (max wait timeout)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
									}
									break;
								case PriorityType.Yield:
#if DEBUG
									if (debug)
										Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): YIELD sign. waittime={vehicleState.waitTime}");
#endif

									if (vehicleState.waitTime < GlobalConfig.Instance.MaxPriorityWaitTime) {
										vehicleState.waitTime++;
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to STOP (wait)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Stop;

										if (sqrSpeed <= TrafficPriorityManager.MAX_SQR_YIELD_VELOCITY || Options.simAccuracy <= 2) {
											if (Options.simAccuracy >= 4) {
												vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
											} else {
												hasPriority = prioMan.HasPriority(frontVehicleId, ref vehicleData, ref prevPos, targetNodeId, isTargetStartNode, ref position, ref targetNode);
#if DEBUG
												if (debug)
													Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): hasPriority: {hasPriority}");
#endif

												if (!hasPriority) {
													vehicleData.m_blockCounter = 0;
													maxSpeed = 0f;
													return false;
												} else {
#if DEBUG
													if (debug)
														Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to LEAVE (no incoming cars)");
#endif
													vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
												}
											}
										} else {
#if DEBUG
											if (debug)
												Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Vehicle has not yet reached yield speed (reduce {sqrSpeed} by {vehicleState.reduceSqrSpeedByValueToYield})");
#endif

											// vehicle has not yet reached yield speed
											maxSpeed = TrafficPriorityManager.MAX_YIELD_VELOCITY;
											return false;
										}
									} else {
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to LEAVE (max wait timeout)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
									}
									break;
								case PriorityType.Main:
								default:
#if DEBUG
									if (debug)
										Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): MAIN sign. waittime={vehicleState.waitTime}");
#endif
									maxSpeed = 0f;

									if (Options.simAccuracy == 4)
										return true;

									if (vehicleState.waitTime < GlobalConfig.Instance.MaxPriorityWaitTime) {
										vehicleState.waitTime++;
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to STOP (wait)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Stop;

										hasPriority = prioMan.HasPriority(frontVehicleId, ref vehicleData, ref prevPos, targetNodeId, isTargetStartNode, ref position, ref targetNode);
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): {hasPriority}");
#endif

										if (!hasPriority) {
											vehicleData.m_blockCounter = 0;
											return false;
										}
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState to LEAVE (no conflicting car)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
									} else {
#if DEBUG
										if (debug)
											Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Max. wait time exceeded. Setting JunctionTransitState to LEAVE (max wait timeout)");
#endif
										vehicleState.JunctionTransitState = VehicleJunctionTransitState.Leave;
									}
									return true;
							}
						} else if (sqrSpeed <= TrafficPriorityManager.MAX_SQR_STOP_VELOCITY && (vehicleState.vehicleType & ExtVehicleType.RoadVehicle) != ExtVehicleType.None) {
							// vehicle is not moving. reset allowance to leave junction
#if DEBUG
							if (debug)
								Log._Debug($"VehicleBehaviorManager.MayChangeSegment({frontVehicleId}): Setting JunctionTransitState from LEAVE to BLOCKED (speed to low)");
#endif
							vehicleState.JunctionTransitState = VehicleJunctionTransitState.Blocked;

							maxSpeed = 0f;
							return false;
						}
					}
				}
			}
			maxSpeed = 0f; // maxSpeed should be set by caller
			return true;
		}

		public bool MayDespawn(ref Vehicle vehicleData) {
			return Options.enableDespawning || vehicleData.m_flags2 != 0;
		}

		public float CalcMaxSpeed(ushort vehicleId, VehicleInfo vehicleInfo, PathUnit.Position position, ref NetSegment segment, Vector3 pos, float maxSpeed, bool isRecklessDriver) {
			if (Singleton<NetManager>.instance.m_treatWetAsSnow) {
				DistrictManager districtManager = Singleton<DistrictManager>.instance;
				byte district = districtManager.GetDistrict(pos);
				DistrictPolicies.CityPlanning cityPlanningPolicies = districtManager.m_districts.m_buffer[(int)district].m_cityPlanningPolicies;
				if ((cityPlanningPolicies & DistrictPolicies.CityPlanning.StuddedTires) != DistrictPolicies.CityPlanning.None) {
					if (Options.strongerRoadConditionEffects) {
						if (maxSpeed > ICY_ROADS_STUDDED_MIN_SPEED)
							maxSpeed = ICY_ROADS_STUDDED_MIN_SPEED + (float)(255 - segment.m_wetness) * 0.0039215686f * (maxSpeed - ICY_ROADS_STUDDED_MIN_SPEED);
					} else {
						maxSpeed *= 1f - (float)segment.m_wetness * 0.0005882353f; // vanilla: -15% .. ±0%
					}
					districtManager.m_districts.m_buffer[(int)district].m_cityPlanningPoliciesEffect |= DistrictPolicies.CityPlanning.StuddedTires;
				} else {
					if (Options.strongerRoadConditionEffects) {
						if (maxSpeed > ICY_ROADS_MIN_SPEED)
							maxSpeed = ICY_ROADS_MIN_SPEED + (float)(255 - segment.m_wetness) * 0.0039215686f * (maxSpeed - ICY_ROADS_MIN_SPEED);
					} else {
						maxSpeed *= 1f - (float)segment.m_wetness * 0.00117647066f; // vanilla: -30% .. ±0%
					}
				}
			} else {
				if (Options.strongerRoadConditionEffects) {
					float minSpeed = Math.Min(maxSpeed * WET_ROADS_FACTOR, WET_ROADS_MAX_SPEED);
					if (maxSpeed > minSpeed)
						maxSpeed = minSpeed + (float)(255 - segment.m_wetness) * 0.0039215686f * (maxSpeed - minSpeed);
				} else {
					maxSpeed *= 1f - (float)segment.m_wetness * 0.0005882353f; // vanilla: -15% .. ±0%
				}
			}

			if (Options.strongerRoadConditionEffects) {
				float minSpeed = Math.Min(maxSpeed * BROKEN_ROADS_FACTOR, BROKEN_ROADS_MAX_SPEED);
				if (maxSpeed > minSpeed) {
					maxSpeed = minSpeed + (float)segment.m_condition * 0.0039215686f * (maxSpeed - minSpeed);
				}
			} else {
				maxSpeed *= 1f + (float)segment.m_condition * 0.0005882353f; // vanilla: ±0% .. +15 %
			}
			
			maxSpeed = ApplyRealisticSpeeds(maxSpeed, vehicleId, vehicleInfo, isRecklessDriver);
			maxSpeed = Math.Max(MIN_SPEED, maxSpeed); // at least 10 km/h

			return maxSpeed;
		}

		public uint GetVehicleRand(ushort vehicleId) {
			return (uint)(vehicleId % 100);
		}

		public float ApplyRealisticSpeeds(float speed, ushort vehicleId, VehicleInfo vehicleInfo, bool isRecklessDriver) {
			if (Options.realisticSpeeds) {
				float vehicleRand = 0.01f * (float)GetVehicleRand(vehicleId);
				if (vehicleInfo.m_isLargeVehicle)
					speed *= 0.9f + vehicleRand * 0.1f; // a little variance, 0.9 .. 1
				else if (isRecklessDriver)
					speed *= 1.3f + vehicleRand * 1.7f; // woohooo, 1.3 .. 3
				else
					speed *= 0.8f + vehicleRand * 0.5f; // a little variance, 0.8 .. 1.3
			} else {
				if (isRecklessDriver)
					speed *= 1.5f;
			}
			return speed;
		}

		public int FindBestLane(ushort vehicleId, ref Vehicle vehicleData, ref VehicleState vehicleState, uint currentLaneId, PathUnit.Position currentPathPos, NetInfo currentSegInfo, PathUnit.Position next1PathPos, NetInfo next1SegInfo, PathUnit.Position next2PathPos, PathUnit.Position next3PathPos, PathUnit.Position next4PathPos) {
			GlobalConfig conf = GlobalConfig.Instance;
#if DEBUG
			bool debug = conf.DebugSwitches[17] && (conf.DebugVehicleId == 0 || conf.DebugVehicleId == vehicleId);
			if (debug) {
				Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): currentLaneId={currentLaneId}, currentPathPos=[seg={currentPathPos.m_segment}, lane={currentPathPos.m_lane}, off={currentPathPos.m_offset}] next1PathPos=[seg={next1PathPos.m_segment}, lane={next1PathPos.m_lane}, off={next1PathPos.m_offset}] next2PathPos=[seg={next2PathPos.m_segment}, lane={next2PathPos.m_lane}, off={next2PathPos.m_offset}] next3PathPos=[seg={next3PathPos.m_segment}, lane={next3PathPos.m_lane}, off={next3PathPos.m_offset}] next4PathPos=[seg={next4PathPos.m_segment}, lane={next4PathPos.m_lane}, off={next4PathPos.m_offset}]");
			}
#endif

			if (vehicleState.lastAltLaneSelSegmentId == currentPathPos.m_segment) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping alternative lane selection: Already calculated.");
				}
#endif
				return next1PathPos.m_lane;
			}
			vehicleState.lastAltLaneSelSegmentId = currentPathPos.m_segment;

			bool recklessDriver = VehicleStateManager.Instance.IsRecklessDriver(vehicleId, ref vehicleData);

			// cur -> next1
			float vehicleLength = 1f + vehicleState.totalLength;
			bool startNode = currentPathPos.m_offset < 128;
			uint currentFwdRoutingIndex = RoutingManager.Instance.GetLaneEndRoutingIndex(currentLaneId, startNode);

			if (! RoutingManager.Instance.laneEndForwardRoutings[currentFwdRoutingIndex].routed) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): No forward routing for next path position available.");
				}
#endif
				return next1PathPos.m_lane;
			}

			LaneTransitionData[] currentFwdTransitions = RoutingManager.Instance.laneEndForwardRoutings[currentFwdRoutingIndex].transitions;

			if (currentFwdTransitions == null) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): No forward transitions found for current lane {currentLaneId} at startNode {startNode}.");
				}
#endif
				return next1PathPos.m_lane;
			}

			VehicleInfo vehicleInfo = vehicleData.Info;
			float vehicleMaxSpeed = vehicleInfo.m_maxSpeed / 8f;

			float bestStayMeanSpeed = 0f;
			float bestStaySpeedDiff = float.PositiveInfinity; // best speed difference on next continuous lane
			int bestStayTotalLaneDist = int.MaxValue;
			byte bestStayNext1LaneIndex = next1PathPos.m_lane;

			float bestOptMeanSpeed = 0f;
			float bestOptSpeedDiff = float.PositiveInfinity; // best speed difference on all next lanes
			int bestOptTotalLaneDist = int.MaxValue;
			byte bestOptNext1LaneIndex = next1PathPos.m_lane;

			bool foundSafeLaneChange = false;
			bool foundClearBackLane = false;
			bool foundClearFwdLane = false;

			for (int i = 0; i < currentFwdTransitions.Length; ++i) {
				if (currentFwdTransitions[i].segmentId != next1PathPos.m_segment) {
					continue;
				}

				if (!(currentFwdTransitions[i].type == LaneEndTransitionType.Default ||
					currentFwdTransitions[i].type == LaneEndTransitionType.LaneConnection ||
					(recklessDriver && currentFwdTransitions[i].type == LaneEndTransitionType.Relaxed))
				) {
					continue;
				}

				if (currentFwdTransitions[i].distance > 1) {
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping current transition {currentFwdTransitions[i]} (distance too large)");
					}
#endif
					continue;
				}

				int minTotalLaneDist = int.MaxValue;

				// next1 -> next2
				if (next2PathPos.m_segment != 0) {
					uint next1FwdRoutingIndex = RoutingManager.Instance.GetLaneEndRoutingIndex(currentFwdTransitions[i].laneId, !currentFwdTransitions[i].startNode);
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Exploring transitions for next1 lane id={currentFwdTransitions[i].laneId}, seg.={currentFwdTransitions[i].segmentId}, index={currentFwdTransitions[i].laneIndex}, startNode={!currentFwdTransitions[i].startNode}: {RoutingManager.Instance.laneEndForwardRoutings[next1FwdRoutingIndex]}");
					}
#endif
					if (!RoutingManager.Instance.laneEndForwardRoutings[next1FwdRoutingIndex].routed) {
						continue;
					}
					LaneTransitionData[] next1FwdTransitions = RoutingManager.Instance.laneEndForwardRoutings[next1FwdRoutingIndex].transitions;

					if (next1FwdTransitions == null) {
						continue;
					}

					int trans3Index = -1;
					for (int j = 0; j < next1FwdTransitions.Length; ++j) {
						if (next1FwdTransitions[j].segmentId != next2PathPos.m_segment) {
							continue;
						}

						if (!(next1FwdTransitions[j].type == LaneEndTransitionType.Default ||
							next1FwdTransitions[j].type == LaneEndTransitionType.LaneConnection ||
							(recklessDriver && next1FwdTransitions[j].type == LaneEndTransitionType.Relaxed))
						) {
							continue;
						}

						if (next1FwdTransitions[j].distance > 1) {
#if DEBUG
							if (debug) {
								Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping next1 transition {next1FwdTransitions[j]} (distance too large)");
							}
#endif
							continue;
						}

						if (next3PathPos.m_segment != 0) {
							// next2 -> next3
							uint next2FwdRoutingIndex = RoutingManager.Instance.GetLaneEndRoutingIndex(next1FwdTransitions[j].laneId, !next1FwdTransitions[j].startNode);
#if DEBUG
							if (debug) {
								Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Exploring transitions for next2 lane id={next1FwdTransitions[j].laneId}, seg.={next1FwdTransitions[j].segmentId}, index={next1FwdTransitions[j].laneIndex}, startNode={!next1FwdTransitions[j].startNode}: {RoutingManager.Instance.laneEndForwardRoutings[next2FwdRoutingIndex]}");
							}
#endif
							if (!RoutingManager.Instance.laneEndForwardRoutings[next2FwdRoutingIndex].routed) {
								continue;
							}
							LaneTransitionData[] next2FwdTransitions = RoutingManager.Instance.laneEndForwardRoutings[next2FwdRoutingIndex].transitions;

							if (next2FwdTransitions == null) {
								continue;
							}

							for (int k = 0; k < next2FwdTransitions.Length; ++k) {
								if (next2FwdTransitions[k].segmentId != next3PathPos.m_segment) {
									continue;
								}

								if (!(next2FwdTransitions[k].type == LaneEndTransitionType.Default ||
									next2FwdTransitions[k].type == LaneEndTransitionType.LaneConnection ||
									(recklessDriver && next2FwdTransitions[k].type == LaneEndTransitionType.Relaxed))
								) {
									continue;
								}

								if (next2FwdTransitions[k].distance > 1) {
#if DEBUG
									if (debug) {
										Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping next2 transition {next2FwdTransitions[k]} (distance too large)");
									}
#endif
									continue;
								}

								if (next4PathPos.m_segment != 0) {
									// next3 -> next4
									uint next3FwdRoutingIndex = RoutingManager.Instance.GetLaneEndRoutingIndex(next2FwdTransitions[k].laneId, !next2FwdTransitions[k].startNode);
#if DEBUG
									if (debug) {
										Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Exploring transitions for next3 lane id={next2FwdTransitions[k].laneId}, seg.={next2FwdTransitions[k].segmentId}, index={next2FwdTransitions[k].laneIndex}, startNode={!next2FwdTransitions[k].startNode}: {RoutingManager.Instance.laneEndForwardRoutings[next3FwdRoutingIndex]}");
									}
#endif
									if (!RoutingManager.Instance.laneEndForwardRoutings[next3FwdRoutingIndex].routed) {
										continue;
									}
									LaneTransitionData[] next3FwdTransitions = RoutingManager.Instance.laneEndForwardRoutings[next3FwdRoutingIndex].transitions;

									if (next3FwdTransitions == null) {
										continue;
									}

									// check if original next4 lane is accessible via the next3 lane
									for (int l = 0; l < next3FwdTransitions.Length; ++l) {
										if (next3FwdTransitions[l].segmentId != next4PathPos.m_segment) {
											continue;
										}

										if (!(next3FwdTransitions[l].type == LaneEndTransitionType.Default ||
											next3FwdTransitions[l].type == LaneEndTransitionType.LaneConnection ||
											(recklessDriver && next3FwdTransitions[l].type == LaneEndTransitionType.Relaxed))
										) {
											continue;
										}

										if (next3FwdTransitions[l].distance > 1) {
#if DEBUG
											if (debug) {
												Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping next3 transition {next3FwdTransitions[l]} (distance too large)");
											}
#endif
											continue;
										}

										if (next3FwdTransitions[l].laneIndex == next4PathPos.m_lane) {
											trans3Index = l;
											int totalLaneDist = next1FwdTransitions[j].distance + next2FwdTransitions[k].distance + next3FwdTransitions[l].distance;
											if (totalLaneDist < minTotalLaneDist) {
												minTotalLaneDist = totalLaneDist;
											}
											break;
										}
									}

									if (trans3Index >= 0) {
										// we found a valid routing from [current lane] (currentPathPos) to [next1 lane] (next1Pos), [next2 lane] (next2Pos), [next3 lane] (next3Pos), and [next4 lane] (next4Pos)
#if DEBUG
										if (debug) {
											Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Found candidate transition: {currentLaneId} -> {currentFwdTransitions[i]} -> {next1FwdTransitions[j]} -> {next2FwdTransitions[k]} -> {next3FwdTransitions[trans3Index]}");
										}
#endif
									}
								}
							}
						}
					}

					if (trans3Index < 0) {
						continue;
					}
				}

				// This lane is a valid candidate.

				/*
				 * Check if next1 lane is clear
				 */
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Checking for traffic on next1 lane id={currentFwdTransitions[i].laneId}.");
				}
#endif

				bool laneChange = currentFwdTransitions[i].distance != 0;
				bool next1LaneClear = true;
				if (laneChange) {
					/*
					 * check for traffic on next1 lane
					 */
					float reservedSpace = 0;
					Services.NetService.ProcessLane(currentFwdTransitions[i].laneId, delegate (uint next1LaneId, ref NetLane next1Lane) {
						reservedSpace = next1Lane.GetReservedSpace();
						return true;
					});

					if (currentFwdTransitions[i].laneIndex == next1PathPos.m_lane) {
						reservedSpace -= vehicleLength;
					}

					next1LaneClear = reservedSpace <= (recklessDriver ? conf.AltLaneSelectionMaxRecklessReservedSpace : conf.AltLaneSelectionMaxReservedSpace);
				}

				if (foundClearFwdLane && ! next1LaneClear) {
					continue;
				}

				/*
				 * Check traffic on the lanes in front of the candidate lane in order to prevent vehicles from backing up traffic
				 */
				bool prevLanesClear = true;
				if (laneChange) {
					uint next1BackRoutingIndex = RoutingManager.Instance.GetLaneEndRoutingIndex(currentFwdTransitions[i].laneId, currentFwdTransitions[i].startNode);
					if (!RoutingManager.Instance.laneEndBackwardRoutings[next1BackRoutingIndex].routed) {
						continue;
					}
					LaneTransitionData[] next1BackTransitions = RoutingManager.Instance.laneEndBackwardRoutings[next1BackRoutingIndex].transitions;

					if (next1BackTransitions == null) {
						continue;
					}

					for (int j = 0; j < next1BackTransitions.Length; ++j) {
						if (next1BackTransitions[j].segmentId != currentPathPos.m_segment ||
							next1BackTransitions[j].laneIndex == currentPathPos.m_lane) {
							continue;
						}

						if (!(next1BackTransitions[j].type == LaneEndTransitionType.Default ||
							next1BackTransitions[j].type == LaneEndTransitionType.LaneConnection ||
							(recklessDriver && next1BackTransitions[j].type == LaneEndTransitionType.Relaxed))
						) {
							continue;
						}

						if (next1BackTransitions[j].distance > 1) {
#if DEBUG
							if (debug) {
								Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Skipping next1 backward transition {next1BackTransitions[j]} (distance too large)");
							}
#endif
							continue;
						}

#if DEBUG
						if (debug) {
							Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Checking for upcoming traffic in front of next1 lane id={currentFwdTransitions[i].laneId}. Checking back transition {next1BackTransitions[j]}");
						}
#endif

						Services.NetService.ProcessLane(next1BackTransitions[j].laneId, delegate (uint prevLaneId, ref NetLane prevLane) {
							prevLanesClear = prevLane.GetReservedSpace() <= (recklessDriver ? conf.AltLaneSelectionMaxRecklessReservedSpace : conf.AltLaneSelectionMaxReservedSpace);
							return true;
						});

						if (!prevLanesClear) {
#if DEBUG
							if (debug) {
								Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Back lane {next1BackTransitions[j].laneId} is not clear!");
							}
#endif
							break;
						} else {
#if DEBUG
							if (debug) {
								Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Back lane {next1BackTransitions[j].laneId} is clear!");
							}
#endif
						}
					}
				}

#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Checking for coming up traffic in front of next1 lane. prevLanesClear={prevLanesClear}");
				}
#endif

				if (foundClearBackLane && ! prevLanesClear) {
					continue;
				}

				// calculate lane metric
				NetInfo.Lane next1LaneInfo = next1SegInfo.m_lanes[currentFwdTransitions[i].laneIndex];
				float next1MaxSpeed = SpeedLimitManager.Instance.GetLockFreeGameSpeedLimit(currentFwdTransitions[i].segmentId, currentFwdTransitions[i].laneIndex, currentFwdTransitions[i].laneId, next1LaneInfo);
				float targetSpeed = Math.Min(vehicleMaxSpeed, ApplyRealisticSpeeds(next1MaxSpeed, vehicleId, vehicleInfo, recklessDriver));

				TrafficMeasurementManager.LaneTrafficData next1LaneTrafficData;
				TrafficMeasurementManager.Instance.GetLaneTrafficData(currentFwdTransitions[i].segmentId, currentFwdTransitions[i].laneIndex, out next1LaneTrafficData);

				float relMeanSpeedInPercent = next1LaneTrafficData.meanSpeed / (TrafficMeasurementManager.REF_REL_SPEED / TrafficMeasurementManager.REF_REL_SPEED_PERCENT_DENOMINATOR);
				float randSpeed = 0f;
				if (conf.AltLaneSelectionLaneSpeedRandInterval > 0) {
					randSpeed = Services.SimulationService.Randomizer.Int32((uint)conf.AltLaneSelectionLaneSpeedRandInterval + 1u) - conf.AltLaneSelectionLaneSpeedRandInterval / 2f;
					relMeanSpeedInPercent += randSpeed;
				}

				float relMeanSpeed = relMeanSpeedInPercent / (float)TrafficMeasurementManager.REF_REL_SPEED_PERCENT_DENOMINATOR;
				float next1MeanSpeed = relMeanSpeed * next1MaxSpeed;

				if (
#if DEBUG
					conf.DebugSwitches[19] &&
#endif
					next1LaneInfo.m_similarLaneCount > 1) {
					float relLaneInnerIndex = ((float)RoutingManager.Instance.CalcOuterSimilarLaneIndex(next1LaneInfo) / (float)next1LaneInfo.m_similarLaneCount);
					float rightObligationFactor = conf.AltLaneSelectionMostOuterLaneSpeedFactor + (conf.AltLaneSelectionMostInnerLaneSpeedFactor - conf.AltLaneSelectionMostOuterLaneSpeedFactor) * relLaneInnerIndex;
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Applying obligation factor to next1 lane {currentFwdTransitions[i].laneId}: relLaneInnerIndex={relLaneInnerIndex}, rightObligationFactor={rightObligationFactor}, next1MaxSpeed={next1MaxSpeed}, relMeanSpeedInPercent={relMeanSpeedInPercent}, randSpeed={randSpeed}, next1MeanSpeed={next1MeanSpeed} => new next1MeanSpeed={Mathf.Max(rightObligationFactor * next1MaxSpeed, next1MeanSpeed)}");
					}
#endif
					next1MeanSpeed = Mathf.Min(rightObligationFactor * next1MaxSpeed, next1MeanSpeed);
				}

				float speedDiff = next1MeanSpeed - targetSpeed; // > 0: lane is faster than vehicle would go. < 0: vehicle could go faster than this lane allows
				
				if (!laneChange &&
					(float.IsInfinity(bestStaySpeedDiff) ||
					(bestStaySpeedDiff < 0 && speedDiff > bestStaySpeedDiff) ||
					(bestStaySpeedDiff > 0 && speedDiff < bestStaySpeedDiff && speedDiff >= 0))
				) {
					bestStaySpeedDiff = speedDiff;
					bestStayNext1LaneIndex = currentFwdTransitions[i].laneIndex;
					bestStayMeanSpeed = next1MeanSpeed;
					bestStayTotalLaneDist = minTotalLaneDist;
				}
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): Calculated metric for next1 lane {currentFwdTransitions[i].laneId}: next1MaxSpeed={next1MaxSpeed} next1MeanSpeed={next1MeanSpeed} targetSpeed={targetSpeed} speedDiff={speedDiff} bestSpeedDiff={bestOptSpeedDiff} bestStaySpeedDiff={bestStaySpeedDiff}");
				}
#endif
				bool foundFirstClearFwdLane = laneChange && !foundClearFwdLane && next1LaneClear;
				bool foundFirstClearBackLane = laneChange && !foundClearBackLane && prevLanesClear;
				bool foundFirstSafeLaneChange = laneChange && !foundSafeLaneChange && next1LaneClear && prevLanesClear;
				if ((foundFirstClearFwdLane && !foundClearBackLane) ||
					(foundFirstClearBackLane && !foundClearFwdLane) ||
					foundFirstSafeLaneChange ||
					float.IsInfinity(bestOptSpeedDiff) ||
					(bestOptSpeedDiff < 0 && speedDiff > bestOptSpeedDiff) ||
					(bestOptSpeedDiff > 0 && speedDiff < bestOptSpeedDiff && speedDiff >= 0)) {
					bestOptSpeedDiff = speedDiff;
					bestOptNext1LaneIndex = currentFwdTransitions[i].laneIndex;
					bestOptMeanSpeed = next1MeanSpeed;
					bestOptTotalLaneDist = minTotalLaneDist;
				}

				if (foundFirstClearBackLane) {
					foundClearBackLane = true;
				}

				if (foundFirstClearFwdLane) {
					foundClearFwdLane = true;
				}

				if (foundFirstSafeLaneChange) {
					foundSafeLaneChange = true;
				}
			}

#if DEBUG
			if (debug) {
				Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): best lane index: {bestOptNext1LaneIndex}, best stay lane index: {bestStayNext1LaneIndex}, path lane index: {next1PathPos.m_lane})\nbest speed diff: {bestOptSpeedDiff}, best stay speed diff: {bestStaySpeedDiff}\nfoundClearBackLane={foundClearBackLane}, foundClearFwdLane={foundClearFwdLane}, foundSafeLaneChange={foundSafeLaneChange}\nbestMeanSpeed={bestOptMeanSpeed}, bestStayMeanSpeed={bestStayMeanSpeed}");
			}
#endif

			if (float.IsInfinity(bestStaySpeedDiff)) {
				// no continuous lane found
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> no continuous lane found -- selecting bestOptNext1LaneIndex={bestOptNext1LaneIndex}");
				}
#endif
				return bestOptNext1LaneIndex;
			}

			if (float.IsInfinity(bestOptSpeedDiff)) {
				// no lane change found
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> no lane change found -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
				}
#endif
				return bestStayNext1LaneIndex;
			}

			// decide if vehicle should stay or change

			if (bestStaySpeedDiff == 0 || bestOptMeanSpeed < 0.1f) {
				/*
				 * edge cases:
				 *   (1) continuous lane is super optimal
				 *   (2) best mean speed is near zero
				 */
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> edge case: continuous lane is optimal ({bestStaySpeedDiff == 0}) / best mean speed is near zero ({bestOptMeanSpeed < 0.1f}) -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
				}
#endif
				return bestStayNext1LaneIndex;
			}

			if (bestStayTotalLaneDist != bestOptTotalLaneDist && Math.Max(bestStayTotalLaneDist, bestOptTotalLaneDist) > conf.AltLaneSelectionMaxOptLaneChanges) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): maximum best total lane distance = {Math.Max(bestStayTotalLaneDist, bestOptTotalLaneDist)} > AltLaneSelectionMaxOptLaneChanges");
				}
#endif

				if (bestOptTotalLaneDist < bestStayTotalLaneDist) {
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> selecting lane change option for minimizing number of future lane changes -- selecting bestOptNext1LaneIndex={bestOptNext1LaneIndex}");
					}
#endif
					return bestOptNext1LaneIndex;
				} else {
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> selecting stay option for minimizing number of future lane changes -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
					}
#endif
					return bestStayNext1LaneIndex;
				}
			}

			if (foundSafeLaneChange) {
				// safe lane change is possible
				if (bestStaySpeedDiff < 0 && bestOptSpeedDiff > bestStaySpeedDiff) {
					// found a lane change that improves vehicle speed
					float improvement = 100f * ((bestOptSpeedDiff - bestStaySpeedDiff) / ((bestStayMeanSpeed + bestOptMeanSpeed) / 2f));
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): a safe lane change for speed improvement is possible. improvement={improvement}%");
					}
#endif
					if (improvement >= conf.AltLaneSelectionMinSafeSpeedImprovement) {
						// speed improvement is significant
#if DEBUG
						if (debug) {
							Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> found a faster lane to change to and speed improvement is significant -- selecting bestOptNext1LaneIndex={bestOptNext1LaneIndex}");
						}
#endif
						return bestOptNext1LaneIndex;
					}

					// insufficient improvement
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> found a faster lane to change to but speed improvement is NOT significant -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
					}
#endif
					return bestStayNext1LaneIndex;
				} else if (bestStaySpeedDiff > 0 && bestOptSpeedDiff < bestStaySpeedDiff && bestOptSpeedDiff >= 0) {
					// found a lane change that optimizes overall traffic
					float optimization = 100f * ((bestStaySpeedDiff - bestOptSpeedDiff) / ((bestStayMeanSpeed + bestOptMeanSpeed) / 2f));
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): found a lane change that optimizes overall traffic. optimization={optimization}%");
					}
#endif
					if (optimization >= conf.AltLaneSelectionMinSafeTrafficImprovement) {
						// traffic optimization is significant
#if DEBUG
						if (debug) {
							Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> found a lane that optimizes overall traffic and traffic optimization is significant -- selecting bestOptNext1LaneIndex={bestOptNext1LaneIndex}");
						}
#endif
						return bestOptNext1LaneIndex;
					}

					// insufficient optimization
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> found a lane that optimizes overall traffic but optimization is NOT significant -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
					}
#endif
					return bestOptNext1LaneIndex;
				}

				// suboptimal safe lane change
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> suboptimal safe lane change detected -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
				}
#endif
				return bestStayNext1LaneIndex;
			} else {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): unsafe lane change detected");
				}
#endif
				// unsafe lane change possibility detected
				if (bestStaySpeedDiff < 0 && bestOptSpeedDiff > bestStaySpeedDiff) {
					// found a faster unsafe lane to change to
#if DEBUG
					if (debug) {
						Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): found a faster unsafe lane to change to");
					}
#endif
					float meanSpeedDiff = Mathf.Abs(bestOptMeanSpeed - bestStayMeanSpeed);
					if (meanSpeedDiff <= conf.AltLaneSelectionMaxUnsafeSpeedDiff) {
						// mean speed difference within tolerance: allow lane change
#if DEBUG
						if (debug) {
							Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> mean speed difference within tolerance: allow lane change -- selecting bestOptNext1LaneIndex={bestOptNext1LaneIndex}");
						}
#endif
						return bestOptNext1LaneIndex;
					}
				}

#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.FindBestLane({vehicleId}): ===> mean speed difference NOT within tolerance: disallow lane change -- selecting bestStayNext1LaneIndex={bestStayNext1LaneIndex}");
				}
#endif
				return bestStayNext1LaneIndex;
			}
		}

		public bool MayFindBestLane(ushort vehicleId, ref Vehicle vehicleData, ref VehicleState vehicleState) {
			GlobalConfig conf = GlobalConfig.Instance;
#if DEBUG
			bool debug = conf.DebugSwitches[17] && (conf.DebugVehicleId == 0 || conf.DebugVehicleId == vehicleId);
			if (debug) {
				Log._Debug($"VehicleBehaviorManager.MayFindBestLane({vehicleId}) called.");
			}
#endif

			if ((vehicleData.m_flags & Vehicle.Flags.Emergency2) != 0) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.MayFindBestLane({vehicleId}): Allowing lane checking. Vehicle is on emergency.");
				}
#endif
				return true;
			}

			if (! Options.advancedAI) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.MayFindBestLane({vehicleId}): Skipping lane checking. Advanced Vehicle AI is disabled.");
				}
#endif
				return false;
			}

			if ((vehicleState.vehicleType & (ExtVehicleType.RoadVehicle & ~ExtVehicleType.Bus)) == ExtVehicleType.None) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.MayFindBestLane({vehicleId}): Skipping lane checking. vehicleType={vehicleState.vehicleType}");
				}
#endif
				return false;
			}

			uint vehicleRand = GetVehicleRand(vehicleId);

			if (vehicleRand < conf.AltLaneSelectionVehicleRand) {
#if DEBUG
				if (debug) {
					Log._Debug($"VehicleBehaviorManager.MayFindBestLane({vehicleId}): Skipping lane checking (randomization).");
				}
#endif
				return false;
			}

			return true;
		}
	}
}
