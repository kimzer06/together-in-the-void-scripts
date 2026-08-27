using UnityEngine;
using UnityEngine.Splines;
using System.Collections.Generic;
using SplineMeshTools.Core;

namespace SplineMeshTools.Misc
{
    /// <summary>
    /// Custom ConveyorBeltMover ที่ออกแบบมาสำหรับ RollingBoulder โดยเฉพาะ
    /// แก้ปัญหา physics ชนกันเมื่อหินตกลงมาจากที่สูง หรือผ่าน Portal
    /// </summary>
    public class BoulderConveyorBeltMover : MonoBehaviour
    {
        [Tooltip("Assign the Spline Container")]
        [SerializeField] SplineContainer splineContainer;

        [Tooltip("Speed at which objects move along the spline")]
        [SerializeField] float conveyorSpeed = 1.0f;

        [Tooltip("Height Offset for the conveyor. Useful")]
        [SerializeField] float conveyorHeightOffset = 0.0f;

        [Tooltip("Should the objects in the belt snap it's rotation to the tangents of the spline?")]
        [SerializeField] bool snapRotation = false;

        [Tooltip("Should the objects move in the reverse direction of the spline?")]
        [SerializeField] bool reverseDirection = false;

        [Tooltip("Should moving objects preserve momentum once out of the spline?")]
        [SerializeField] bool preserveMomentum = true;

        [Header("Boulder Physics Fixes")]
        [Tooltip("ความเร็วในการ blend ตำแหน่งหินไปยัง spline (ยิ่งสูงยิ่ง snap เร็ว)")]
        [SerializeField, Range(1f, 50f)] float positionBlendSpeed = 15f;

        [Tooltip("ลด velocity แนวดิ่งเมื่อหินเข้า belt (ป้องกันเด้ง)")]
        [SerializeField] bool dampenVerticalVelocity = true;

        [Tooltip("ระยะเวลา stabilize ก่อนเริ่มเคลื่อนที่ตาม spline (วินาที)")]
        [SerializeField, Range(0f, 0.5f)] float stabilizationTime = 0.1f;

        [Tooltip("แรง friction ที่ใช้ช่วง stabilization เพื่อหยุดหินให้นิ่ง")]
        [SerializeField, Range(0f, 1f)] float stabilizationDrag = 0.5f;

        [Header("Obstacle Detection")]
        [Tooltip("ตรวจสอบ obstacle ก่อนเคลื่อนที่ (ป้องกันทะลุ collider)")]
        [SerializeField] bool checkForObstacles = true;

        [Tooltip("รัศมีของ SphereCast สำหรับเช็ค obstacle (ควรใกล้เคียงกับขนาด object)")]
        [SerializeField] float obstacleCheckRadius = 0.3f;

        [Tooltip("Layer ที่ถือว่าเป็น obstacle (ถ้าไม่เลือก จะเช็คทุก layer ยกเว้นตัวเอง)")]
        [SerializeField] LayerMask obstacleLayerMask = ~0;


        private List<Rigidbody> objectsOnBelt = new List<Rigidbody>();

        private Dictionary<Rigidbody, (Spline spline, float position, int collisionCounts, float entryTime, bool stabilized)> objectPositions
            = new Dictionary<Rigidbody, (Spline, float position, int collisionCounts, float entryTime, bool stabilized)>();

        private void Start()
        {
            if (splineContainer == null)
            {
                splineContainer = GetComponent<SplineContainer>();
                if (splineContainer == null)
                    Debug.LogError("Spline Container must be assigned");
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.contacts[0].point.y > (transform.position.y + conveyorHeightOffset))
            {
                Rigidbody rb = collision.rigidbody;
                if (rb != null)
                {
                    // Find the closest spline and its closest position on that spline
                    (Spline closestSpline, float closestPosition) = SplineMeshUtils.FindClosestSplineAndPosition(splineContainer, collision.transform.position);

                    if (closestSpline != null)
                    {
                        if (!objectsOnBelt.Contains(rb))
                        {
                            objectsOnBelt.Add(rb);
                            objectPositions[rb] = (closestSpline, closestPosition, 1, Time.time, false);

                            // Dampen vertical velocity ทันทีที่เข้า belt
                            if (dampenVerticalVelocity)
                            {
                                Vector3 vel = rb.linearVelocity;
                                // ลด velocity แนวดิ่งเหลือ 0 หรือเกือบ 0
                                vel.y = Mathf.Min(vel.y, 0f) * 0.1f;
                                rb.linearVelocity = vel;
                            }
                        }
                        else
                        {
                            var existing = objectPositions[rb];
                            objectPositions[rb] = (closestSpline, closestPosition, existing.collisionCounts + 1, existing.entryTime, existing.stabilized);
                        }
                    }
                }
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            Rigidbody rb = collision.rigidbody;
            if (rb != null && objectsOnBelt.Contains(rb))
            {
                var existing = objectPositions[rb];
                objectPositions[rb] = (existing.spline, existing.position, existing.collisionCounts - 1, existing.entryTime, existing.stabilized);
                if (objectPositions[rb].collisionCounts == 0)
                {
                    objectsOnBelt.Remove(rb);
                    objectPositions.Remove(rb);
                }
            }
        }

        private void FixedUpdate()
        {
            for (int i = objectsOnBelt.Count - 1; i >= 0; i--)
            {
                var rb = objectsOnBelt[i];

                // cleanup: ถ้า rb ถูกทำลาย, GO ถูก disable, หรือ collider ทั้งหมดถูกปิด → เอาออกจาก belt
                if (rb == null || !rb.gameObject.activeInHierarchy || !HasActiveCollider(rb))
                {
                    objectsOnBelt.RemoveAt(i);
                    if (rb != null) objectPositions.Remove(rb);
                    continue;
                }

                (Spline spline, float position, int collisionCount, float entryTime, bool stabilized) = objectPositions[rb];

                float timeSinceEntry = Time.time - entryTime;

                // ช่วง stabilization: ลด velocity ก่อนเริ่มเคลื่อนที่
                if (!stabilized && timeSinceEntry < stabilizationTime)
                {
                    // ใช้ drag เพื่อลดความเร็ว
                    rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, Vector3.zero, stabilizationDrag);
                    rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Vector3.zero, stabilizationDrag * 0.5f);

                    // อัปเดต position บน spline ให้ตรงกับตำแหน่งปัจจุบัน
                    (Spline updatedSpline, float updatedPosition) = SplineMeshUtils.FindClosestSplineAndPosition(splineContainer, rb.position);
                    if (updatedSpline != null)
                    {
                        objectPositions[rb] = (updatedSpline, updatedPosition, collisionCount, entryTime, false);
                    }
                    continue;
                }

                // Mark as stabilized
                if (!stabilized)
                {
                    // รีเซ็ต velocity อีกครั้งเพื่อให้แน่ใจว่านิ่งแล้ว
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;

                    // หา position ล่าสุดบน spline
                    (Spline updatedSpline, float updatedPosition) = SplineMeshUtils.FindClosestSplineAndPosition(splineContainer, rb.position);
                    if (updatedSpline != null)
                    {
                        objectPositions[rb] = (updatedSpline, updatedPosition, collisionCount, entryTime, true);
                        (spline, position, collisionCount, entryTime, stabilized) = objectPositions[rb];
                    }
                }

                Vector3 direction = spline.EvaluateTangent(position / spline.GetLength());
                int dir = reverseDirection ? -1 : 1;
                direction = direction * dir;
                // Calculate the new position along the spline
                position += dir * conveyorSpeed * Time.fixedDeltaTime;

                bool outOfConveyor = (!reverseDirection && position > spline.GetLength()) || (reverseDirection && (position < 0f));
                if (outOfConveyor)
                {
                    if (preserveMomentum)
                    {
                        // Apply a force in the last known direction to preserve momentum
                        rb.AddForce(direction * conveyorSpeed, ForceMode.VelocityChange);
                    }
                    objectPositions.Remove(rb);
                    objectsOnBelt.RemoveAt(i);
                    continue;
                }

                // Get the position on the spline
                Vector3 splinePosition = spline.EvaluatePosition(position / spline.GetLength());
                // Calculate the final position including height offset
                Vector3 finalPosition = splinePosition + splineContainer.transform.position + Vector3.up * (conveyorHeightOffset);
                finalPosition.y = rb.position.y;

                // Smooth blend แทน snap ตรงๆ เพื่อลด physics conflict
                Vector3 smoothPosition = Vector3.Lerp(rb.position, finalPosition, positionBlendSpeed * Time.fixedDeltaTime);

                // ตรวจสอบ obstacle ก่อน move
                bool obstacleBlocked = false;
                if (checkForObstacles)
                {
                    Vector3 moveDirection = (smoothPosition - rb.position).normalized;
                    float moveDistance = Vector3.Distance(rb.position, smoothPosition);

                    if (moveDistance > 0.001f)
                    {
                        // ใช้ SphereCast เช็คว่ามี obstacle ขวางหรือไม่
                        if (Physics.SphereCast(rb.position, obstacleCheckRadius, moveDirection, out RaycastHit hit, moveDistance + 0.01f, obstacleLayerMask, QueryTriggerInteraction.Ignore))
                        {
                            // ต้องไม่ใช่ตัว conveyor เอง และไม่ใช่ตัว object เอง
                            if (hit.collider.gameObject != gameObject && hit.collider.gameObject != rb.gameObject)
                            {
                                obstacleBlocked = true;
                                // หยุด velocity ไม่ให้ดันต่อ
                                rb.linearVelocity = Vector3.zero;
                            }
                        }
                    }
                }

                if (!obstacleBlocked)
                {
                    rb.MovePosition(smoothPosition);
                }

                if (snapRotation)
                {
                    // Rotate the object while maintaining its original orientation
                    rb.MoveRotation(Quaternion.LookRotation(direction));
                }

                // Update the position in the dictionary (only update position if not blocked)
                if (!obstacleBlocked)
                {
                    objectPositions[rb] = (spline, position, collisionCount, entryTime, stabilized);
                }
            }
        }

        /// <summary>
        /// ตรวจสอบว่า Rigidbody ยังมี Collider ที่ enable อยู่ (ไม่นับ trigger)
        /// </summary>
        private bool HasActiveCollider(Rigidbody rb)
        {
            var colliders = rb.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i].enabled && !colliders[i].isTrigger) return true;
            }
            return false;
        }
    }
}
