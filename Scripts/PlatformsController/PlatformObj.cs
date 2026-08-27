using UnityEngine;
using System.Collections.Generic;

public class PlatformObj : MonoBehaviour
{
    private enum DetectionShape
    {
        Box,
        Sphere,
        Capsule,
        Mesh
    }
    [Header("Pivot Settings")]
    [SerializeField] private Transform pivot;
    [Header("Detection Settings")]
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Vector3 boxSize = Vector3.one;
    [SerializeField] private float sphereRadius = 0.5f;
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;
    [SerializeField] private Mesh meshPreview;

    [Header("Gizmos Settings")]
    [SerializeField] private Color gizmoColor = new Color(0f, 1f, 0f, 0.25f);

    [SerializeField] private List<Rigidbody> trackedRigidbodies = new List<Rigidbody>();
    private Vector3 lastPosition;

    private void Start()
    {
        lastPosition = transform.position;
    }

    private void Update()
    {
        DetectAndMoveRigidbodies();
        MoveTrackedRigidbodies();
        lastPosition = transform.position;
    }

    private void DetectAndMoveRigidbodies()
    {
        if (pivot == null) pivot = transform;

        Collider[] detectedColliders = null;
        Vector3 worldPos = transform.position + detectPosition;
        Quaternion worldRot = pivot.rotation;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                detectedColliders = Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot);
                break;
            case DetectionShape.Sphere:
                detectedColliders = Physics.OverlapSphere(worldPos, sphereRadius);
                break;
            case DetectionShape.Capsule:
                Vector3 point1 = worldPos + pivot.up * Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 point2 = worldPos - pivot.up * Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                detectedColliders = Physics.OverlapCapsule(point1, point2, capsuleRadius);
                break;
            case DetectionShape.Mesh:
                detectedColliders = new Collider[0];
                break;
        }

        // Remove rigidbodies ที่ออกจาก Detection
        for (int i = trackedRigidbodies.Count - 1; i >= 0; i--)
        {
            if (detectedColliders == null || !System.Array.Exists(detectedColliders, col => col.attachedRigidbody == trackedRigidbodies[i]))
            {
                trackedRigidbodies.RemoveAt(i);
            }
        }

        if (detectedColliders == null) return;

        // Add rigidbodies ใหม่ที่เข้ามาใน Detection
        foreach (var col in detectedColliders)
        {
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !trackedRigidbodies.Contains(rb))
            {
                trackedRigidbodies.Add(rb);
            }
        }
    }

    private void MoveTrackedRigidbodies()
    {
        Vector3 delta = transform.position - lastPosition;
        if (delta == Vector3.zero) return;

        foreach (var rb in trackedRigidbodies)
        {
            // MovePosition ทำให้ Rigidbody เคลื่อนที่ตาม PlatformObj
            rb.MovePosition(rb.position + delta);
        }
    }

    private void OnDrawGizmos()
    {
        if (pivot == null) pivot = transform;

        Gizmos.color = gizmoColor;
        Matrix4x4 rotationMatrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
        Gizmos.matrix = rotationMatrix;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawCube(detectPosition, boxSize);
                Gizmos.DrawWireCube(detectPosition, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawSphere(detectPosition, sphereRadius);
                Gizmos.DrawWireSphere(detectPosition, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float halfHeight = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * halfHeight;
                Gizmos.DrawWireSphere(detectPosition + up, capsuleRadius);
                Gizmos.DrawWireSphere(detectPosition - up, capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.forward * capsuleRadius, detectPosition - up + Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.forward * capsuleRadius, detectPosition - up - Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.right * capsuleRadius, detectPosition - up + Vector3.right * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.right * capsuleRadius, detectPosition - up - Vector3.right * capsuleRadius);
                break;
            case DetectionShape.Mesh:
                if (meshPreview != null)
                {
                    Gizmos.DrawMesh(meshPreview, detectPosition, Quaternion.identity, Vector3.one);
                    Gizmos.DrawWireMesh(meshPreview, detectPosition, Quaternion.identity, Vector3.one);
                }
                break;
        }
    }
}
