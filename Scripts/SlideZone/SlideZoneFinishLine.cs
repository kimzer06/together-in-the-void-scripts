using UnityEngine;
using Unity.Netcode;

/// <summary>
/// เส้นชัยสำหรับ Slide Zone - เมื่อผู้เล่นผ่านจะออกจาก slide mode
/// </summary>
[RequireComponent(typeof(Collider))]
public class SlideZoneFinishLine : MonoBehaviour
{
    [Tooltip("SplineSlideZone (ถ้าใช้แบบ Spline)")]
    public SplineSlideZone splineSlideZone;
    
    [Header("Effects (Optional)")]
    [Tooltip("Particle เมื่อผ่านเส้นชัย")]
    public ParticleSystem finishParticle;
    
    [Tooltip("เสียงเมื่อผ่านเส้นชัย")]
    public AudioSource finishSound;
    
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
        
        // ตรวจสอบว่าเป็น player
        var controller = other.GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
        if (controller == null) return;
        
        ulong clientId = netObj.OwnerClientId;
        
        // ตรวจสอบว่าอยู่ใน active slide
        if (_parentZone != null && _parentZone.IsPlayerInActiveSlide(clientId))
        {
            Debug.Log($"[SlideZoneFinishLine] Player {clientId} crossed finish line!");
            
            // เล่น effects
            if (finishParticle != null) finishParticle.Play();
            if (finishSound != null) finishSound.Play();
            
            // ออกจาก slide mode
            controller.SetSlideModeServerRpc(false, default);
        }
    }
    
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        
        var col = GetComponent<Collider>();
        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
        }
    }
#endif
}
