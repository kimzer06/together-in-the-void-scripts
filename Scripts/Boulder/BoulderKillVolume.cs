using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class BoulderKillVolume : NetworkBehaviour
{
    public enum DetectionShape { Box, Sphere, Capsule, Mesh } // Mesh ใช้แค่วาดกิซโม

    [Header("Detection")]
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Transform pivot;
    [SerializeField] private Vector3 detectPosition = Vector3.zero; // local (ของ pivot ถ้ามี ไม่งั้นของตัวเอง)
    [SerializeField] private Vector3 boxSize = new Vector3(2, 2, 2);
    [SerializeField] private float sphereRadius = 1f;
    [SerializeField] private float capsuleRadius = 0.7f;
    [SerializeField] private float capsuleHeight = 2.0f;
    [SerializeField] private Mesh meshPreview;
    [SerializeField] private LayerMask hitMask = ~0;
    [Tooltip("แท็กที่อนุญาตให้ถูกทำลาย (เช่น Boulder)")]
    [SerializeField] private string[] destroyTags = new string[] { "Boulder" };

    [Header("Tick")]
    [SerializeField, Min(0.02f)] private float checkInterval = 0.05f;

    [Header("Gizmos")]
    [SerializeField] private bool showGizmos = true;
    [SerializeField] private Color gizmoColor = new Color(0f, 0.6f, 1f, 0.25f);

    float _nextCheck;

    private Transform Pivot => pivot ? pivot : transform;

    void Update()
    {
        if (!IsServer) return; // เซิร์ฟเท่านั้น
        if (Time.time < _nextCheck) return;
        _nextCheck = Time.time + checkInterval;

        Vector3 worldPos = Pivot.TransformPoint(detectPosition);
        Quaternion worldRot = Pivot.rotation;

        Collider[] cols = null;
        switch (detectionShape)
        {
            case DetectionShape.Box:
                cols = Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, hitMask, QueryTriggerInteraction.Ignore);
                break;
            case DetectionShape.Sphere:
                cols = Physics.OverlapSphere(worldPos, sphereRadius, hitMask, QueryTriggerInteraction.Ignore);
                break;
            case DetectionShape.Capsule:
                {
                    Vector3 up = worldRot * Vector3.up;
                    float half = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                    Vector3 p1 = worldPos + up * half;
                    Vector3 p2 = worldPos - up * half;
                    cols = Physics.OverlapCapsule(p1, p2, capsuleRadius, hitMask, QueryTriggerInteraction.Ignore);
                }
                break;
            case DetectionShape.Mesh:
                // ไม่รองรับตรวจจริง — ใช้เป็นกิซโมพรีวิว
                cols = null;
                break;
        }

        if (cols == null || cols.Length == 0) return;

        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (!c) continue;
            if (!MatchesTags(c.gameObject)) continue;

            var boulder = c.attachedRigidbody ? c.attachedRigidbody.GetComponent<RollingBoulder>()
                                              : c.GetComponentInParent<RollingBoulder>();
            if (boulder && boulder.IsSpawned)
                boulder.KillImmediateServer();
            else
            {
                var no = c.GetComponentInParent<NetworkObject>();
                if (no && no.IsSpawned) no.Despawn(false);
                else Destroy(c.gameObject);
            }
        }
    }

    private bool MatchesTags(GameObject go)
    {
        if (destroyTags == null || destroyTags.Length == 0) return false;
        for (int i = 0; i < destroyTags.Length; i++)
        {
            var t = destroyTags[i];
            if (!string.IsNullOrEmpty(t) && go.CompareTag(t)) return true;
        }
        return false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!showGizmos) return;
        var p = Pivot;
        Gizmos.color = gizmoColor;
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(p.TransformPoint(detectPosition), p.rotation, Vector3.one);

        switch (detectionShape)
        {
            case DetectionShape.Box:
                Gizmos.DrawCube(Vector3.zero, boxSize);
                Gizmos.DrawWireCube(Vector3.zero, boxSize);
                break;
            case DetectionShape.Sphere:
                Gizmos.DrawSphere(Vector3.zero, sphereRadius);
                Gizmos.DrawWireSphere(Vector3.zero, sphereRadius);
                break;
            case DetectionShape.Capsule:
                float half = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 up = Vector3.up * half;
                Gizmos.DrawWireSphere(up, capsuleRadius);
                Gizmos.DrawWireSphere(-up, capsuleRadius);
                Gizmos.DrawLine(up + Vector3.forward * capsuleRadius, -up + Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(up - Vector3.forward * capsuleRadius, -up - Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(up + Vector3.right * capsuleRadius, -up + Vector3.right * capsuleRadius);
                Gizmos.DrawLine(up - Vector3.right * capsuleRadius, -up - Vector3.right * capsuleRadius);
                break;
            case DetectionShape.Mesh:
                if (meshPreview)
                {
                    Gizmos.DrawMesh(meshPreview);
                    Gizmos.DrawWireMesh(meshPreview);
                }
                break;
        }

        Gizmos.matrix = old;
    }
#endif
}
