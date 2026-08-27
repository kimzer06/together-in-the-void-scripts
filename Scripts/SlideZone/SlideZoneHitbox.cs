using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// Hitbox สำหรับสิ่งกีดขวางใน Slide Zone (หิน, กำแพง, ฯลฯ)
/// เมื่อผู้เล่นชน → ซ่อนผู้เล่นทันที รอ 5 วิ แล้วเกิดที่เพื่อน
/// ไม่ใช้ PlayerDeath.Kill() แต่ให้ SlideZone เป็นคนจัดการตาย/respawn
/// </summary>
[RequireComponent(typeof(Collider))]
public class SlideZoneHitbox : MonoBehaviour
{
    [Tooltip("SplineSlideZone (ถ้าใช้แบบ Spline)")]
    public SplineSlideZone splineSlideZone;
    
    [Tooltip("ฆ่าเฉพาะผู้เล่นที่อยู่ใน active slide เท่านั้น")]
    public bool onlyKillDuringActiveSlide = true;
    
    [Header("Visual Feedback (Optional)")]
    [Tooltip("Particle ที่เล่นเมื่อผู้เล่นชน")]
    public ParticleSystem hitParticle;
    
    [Tooltip("เสียงเมื่อผู้เล่นชน")]
    public AudioSource hitSound;
    
    private ISlideZone _parentZone;

    // กรณีผู้เล่นเข้าโซนตอนยัง immune: OnTriggerEnter จะ ignore
    // เราเลยต้องจำว่า "ยังอยู่ในโซนและยังไม่ถูก handle" เพื่อให้ OnTriggerStay ลองใหม่ตอน immunity หมด
    private readonly HashSet<ulong> _playersInsideUnhandled = new HashSet<ulong>();
    
    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
        
        // ใช้ field ที่กำหนดก่อน, ถ้าไม่มี → auto-detect จาก parent
        if (splineSlideZone != null)
            _parentZone = splineSlideZone;
        else
            _parentZone = GetComponentInParent<SplineSlideZone>() as ISlideZone;
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!TryGetLocalOwnedPlayer(other, out var netObj, out var playerDeath)) return;

        _playersInsideUnhandled.Add(netObj.OwnerClientId);
        TryHandleHit(other, netObj, playerDeath);
    }

    private void OnTriggerStay(Collider other)
    {
        if (!TryGetLocalOwnedPlayer(other, out var netObj, out var playerDeath)) return;
        if (!_playersInsideUnhandled.Contains(netObj.OwnerClientId)) return;

        TryHandleHit(other, netObj, playerDeath);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.TryGetComponent<NetworkObject>(out var netObj)) return;
        if (!netObj.IsOwner) return;
        if (!other.GetComponent<PlayerDeath>()) return;

        _playersInsideUnhandled.Remove(netObj.OwnerClientId);
    }

    private static bool TryGetLocalOwnedPlayer(Collider other, out NetworkObject netObj, out PlayerDeath playerDeath)
    {
        netObj = null;
        playerDeath = null;

        if (!other.TryGetComponent(out netObj)) return false;
        if (!netObj.IsOwner) return false;
        playerDeath = other.GetComponent<PlayerDeath>();
        return playerDeath != null;
    }

    private void TryHandleHit(Collider other, NetworkObject netObj, PlayerDeath playerDeath)
    {
        // ต้องมี parentZone ถึงจะทำงานได้
        if (_parentZone == null)
        {
            Debug.LogWarning("[SlideZoneHitbox] No parentZone (ISlideZone) assigned!");
            return;
        }

        ulong clientId = netObj.OwnerClientId;

        // ตรวจสอบ respawn immunity (ป้องกันตายซ้ำหลัง teleport)
        if (playerDeath != null && playerDeath.IsRespawnImmune)
            return;

        // ผ่าน immunity แล้ว ถือว่า handle สำเร็จ กัน spam ใน OnTriggerStay
        _playersInsideUnhandled.Remove(clientId);

        // SplineSlideZone: ให้เซิร์ฟเวอร์ตัดสินจาก session / _slideRunStarted (อย่าพึ่ง IsPlayerInActiveSlide หลังถูกลากออกนอกโซน)
        SplineSlideZone splineZone = splineSlideZone;
        if (splineZone == null && _parentZone is SplineSlideZone fromParent)
            splineZone = fromParent;

        if (splineZone != null && splineZone.IsSpawned)
        {
            if (onlyKillDuringActiveSlide && !splineZone.IsPlayerInActiveSlide(clientId))
                Debug.Log($"[SlideZoneHitbox] Player {clientId} local slide inactive — server will still check slide session.");

            playerDeath.Client_DisableForSlideZoneLocal();
            if (hitParticle != null) hitParticle.Play();
            if (hitSound != null) hitSound.Play();
            splineZone.RequestSlideOrNormalDeathFromHazardTrigger();
            return;
        }

        if (onlyKillDuringActiveSlide && !_parentZone.IsPlayerInActiveSlide(clientId))
        {
            Debug.Log($"[SlideZoneHitbox] Player {clientId} hit obstacle but not in active slide. Ignoring.");
            return;
        }

        playerDeath.Client_DisableForSlideZoneLocal();

        Debug.Log($"[SlideZoneHitbox] Player {clientId} hit obstacle!");

        if (hitParticle != null) hitParticle.Play();
        if (hitSound != null) hitSound.Play();

        _parentZone.NotifyPlayerHitObstacle(clientId);
    }
    
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var col = GetComponent<Collider>();
        if (col == null) return;
        
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.4f);
        
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
