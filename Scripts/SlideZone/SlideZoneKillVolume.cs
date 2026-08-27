using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Kill volume สำหรับ Slide Zone: ตกเหวแล้วให้ระบบ SlideZone จัดการตาย/respawn แบบเดียวกับชนสิ่งกีดขวาง
/// </summary>
[RequireComponent(typeof(Collider))]
public class SlideZoneKillVolume : MonoBehaviour
{
    [Tooltip("SplineSlideZone (ถ้าใช้แบบ Spline)")]
    public SplineSlideZone splineSlideZone;

    [Tooltip("ฆ่าเฉพาะผู้เล่นที่อยู่ใน active slide เท่านั้น")]
    public bool onlyKillDuringActiveSlide = true;

    private ISlideZone _parentZone;

    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;

        if (splineSlideZone != null)
            _parentZone = splineSlideZone;
        else
            _parentZone = GetComponentInParent<SplineSlideZone>() as ISlideZone;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.TryGetComponent<NetworkObject>(out var netObj)) return;
        if (!netObj.IsOwner) return;

        if (_parentZone == null)
        {
            Debug.LogWarning("[SlideZoneKillVolume] No parentZone (ISlideZone) assigned!");
            return;
        }

        ulong clientId = netObj.OwnerClientId;

        if (onlyKillDuringActiveSlide && !_parentZone.IsPlayerInActiveSlide(clientId))
            return;

        var playerDeath = other.GetComponent<PlayerDeath>();
        if (playerDeath != null && playerDeath.IsRespawnImmune)
            return;

        // Freeze ทันทีบน client เพื่อกันผู้เล่น/กล้องไหลตาม spline ก่อน RPC จาก server มาถึง
        if (playerDeath != null)
            playerDeath.Client_DisableForSlideZoneLocal();

        _parentZone.NotifyPlayerHitObstacle(clientId);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var col = GetComponent<Collider>();
        if (col == null) return;

        Gizmos.color = new Color(0.9f, 0.1f, 1f, 0.18f);

        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.DrawWireCube(box.center, box.size);
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.DrawSphere(transform.TransformPoint(sphere.center), sphere.radius * transform.lossyScale.x);
            Gizmos.DrawWireSphere(transform.TransformPoint(sphere.center), sphere.radius * transform.lossyScale.x);
        }
        else if (col is CapsuleCollider capsule)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(capsule.center, capsule.radius);
        }
    }
#endif
}

