using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger Zone สำหรับเปิดใช้งาน WindZoneAutoManager เมื่อผู้เล่นเข้าโซน
/// รองรับการตรวจจับผู้เล่นด้วย NetworkObject.IsPlayerObject หรือ Tag
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))]
[AddComponentMenu("WindZone/Trigger")]
public class WindZoneTrigger : NetworkBehaviour
{
    #region Inspector Fields & Enums
    private enum DetectionShape { Box, Sphere, Capsule, Mesh, Cylinder }
    
    [Header("Target Manager")]
    [Tooltip("ลาก WindZoneAutoManager ที่ต้องการเปิดใช้งานมาวางที่นี่")]
    [SerializeField] private WindZoneAutoManager targetManager;
    
    [Header("Trigger Settings")]
    [Tooltip("ถ้าติ๊ก: จะเปิด Manager เมื่อผู้เล่นเข้าโซน (ถ้าไม่ติ๊กจะปิด Manager)")]
    [SerializeField] private bool activateOnEnter = true;
    
    [Tooltip("ถ้าติ๊ก: จะปิด Manager เมื่อผู้เล่นออกจากโซน")]
    [SerializeField] private bool deactivateOnExit = false;
    
    [Tooltip("ถ้าติ๊ก: จะเปิด Manager ครั้งเดียวเมื่อผู้เล่นเข้าโซนครั้งแรก (ไม่เปิดซ้ำ)")]
    [SerializeField] private bool oneTimeOnly = false;
    
    [Header("Player Detection")]
    [Tooltip("Tag ของผู้เล่น (เช่น 'Player') - ถ้าว่างจะตรวจสอบ IsPlayerObject แทน")]
    [SerializeField] private string playerTag = "Player";
    
    [Tooltip("ถ้าติ๊ก: ใช้ NetworkObject.IsPlayerObject ในการตรวจสอบผู้เล่น (แนะนำ)")]
    [SerializeField] private bool useNetworkPlayerCheck = true;
    
    [Header("Detection Settings")]
    [Tooltip("ใช้ Collider ของ GameObject นี้เป็น Trigger (แนะนำ) หรือใช้ Detection Shape แทน")]
    [SerializeField] private bool useAttachedCollider = true;
    
    [Tooltip("Pivot สำหรับ Detection Shape (ถ้าไม่ใช้ useAttachedCollider)")]
    [SerializeField] private Transform pivot;
    
    [Tooltip("ตำแหน่ง Detection (ถ้าไม่ใช้ useAttachedCollider)")]
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    
    [Tooltip("รูปร่าง Detection (ถ้าไม่ใช้ useAttachedCollider)")]
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    
    [Tooltip("Mesh Preview (สำหรับ DetectionShape.Mesh)")]
    [SerializeField] private Mesh meshPreview;
    
    [Tooltip("Layer Mask สำหรับตรวจจับ")]
    [SerializeField] private LayerMask detectLayers = ~0;
    
    [Header("Box Settings")]
    [SerializeField] private Vector3 boxSize = Vector3.one;
    
    [Header("Sphere Settings")]
    [SerializeField] private float sphereRadius = 0.5f;
    
    [Header("Capsule Settings")]
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;
    
    [Header("Cylinder Settings")]
    [SerializeField] private float cylinderRadius = 0.5f;
    [SerializeField] private float cylinderHeight = 2f;
    
    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;
    [SerializeField] private Color gizmoColor = new Color(0f, 1f, 1f, 0.25f);
    [SerializeField] private bool showGizmo = true;
    #endregion
    
    #region Runtime State
    private readonly HashSet<NetworkObject> _playersInside = new();
    private bool _hasBeenActivated = false;
    private Collider _attachedCollider;
    #endregion
    
    #region Unity Lifecycle
    private void Awake()
    {
        if (!pivot) pivot = transform;
        
        // ตรวจสอบ Collider
        if (useAttachedCollider)
        {
            _attachedCollider = GetComponent<Collider>();
            if (_attachedCollider == null)
            {
                Debug.LogWarning($"[WindZoneTrigger] {name} ไม่มี Collider แต่ useAttachedCollider = true", this);
            }
            else
            {
                _attachedCollider.isTrigger = true;
            }
        }
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _hasBeenActivated = false;
        _playersInside.Clear();
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!useAttachedCollider) return;
        if (!IsServer) return;
        
        CheckPlayerEnter(other);
    }
    
    private void OnTriggerExit(Collider other)
    {
        if (!useAttachedCollider) return;
        if (!IsServer) return;
        
        CheckPlayerExit(other);
    }
    
    private void Update()
    {
        if (!IsServer) return;
        if (useAttachedCollider) return; // ใช้ OnTriggerEnter/Exit แทน
        
        // ใช้ Detection Shape แทน Collider
        DetectPlayersWithShape();
    }
    #endregion
    
    #region Detection Logic
    private void DetectPlayersWithShape()
    {
        Collider[] detected = GetOverlaps();
        if (detected == null || detected.Length == 0)
        {
            // ถ้าไม่มีอะไรในโซน ให้ตรวจสอบว่ามีผู้เล่นออกไปหรือไม่
            if (_playersInside.Count > 0 && deactivateOnExit)
            {
                HashSet<NetworkObject> toRemove = new();
                foreach (NetworkObject playerNO in _playersInside)
                {
                    if (playerNO == null || !playerNO.IsSpawned)
                    {
                        toRemove.Add(playerNO);
                    }
                }
                
                foreach (NetworkObject removeNO in toRemove)
                {
                    _playersInside.Remove(removeNO);
                }
                
                if (_playersInside.Count == 0)
                {
                    OnAllPlayersExited();
                }
            }
            return;
        }
        
        // ตรวจสอบผู้เล่นที่เข้ามาใหม่
        foreach (Collider c in detected)
        {
            if (!c) continue;
            
            if (!IsPlayer(c, out NetworkObject playerNO)) continue;
            
            if (!_playersInside.Contains(playerNO))
            {
                _playersInside.Add(playerNO);
                OnPlayerEntered(playerNO);
            }
        }
        
        // ตรวจสอบผู้เล่นที่ออกไป
        if (deactivateOnExit)
        {
            HashSet<NetworkObject> toRemove = new();
            foreach (NetworkObject playerNO in _playersInside)
            {
                if (playerNO == null || !playerNO.IsSpawned)
                {
                    toRemove.Add(playerNO);
                    continue;
                }
                
                bool stillInside = false;
                foreach (Collider c in detected)
                {
                    if (!c) continue;
                    NetworkObject detectedNO = c.GetComponentInParent<NetworkObject>();
                    if (detectedNO == playerNO)
                    {
                        stillInside = true;
                        break;
                    }
                }
                
                if (!stillInside)
                {
                    toRemove.Add(playerNO);
                }
            }
            
            foreach (NetworkObject removeNO in toRemove)
            {
                _playersInside.Remove(removeNO);
            }
            
            if (toRemove.Count > 0 && _playersInside.Count == 0)
            {
                OnAllPlayersExited();
            }
        }
    }
    
    private void CheckPlayerEnter(Collider other)
    {
        if (!IsPlayer(other, out NetworkObject playerNO)) return;
        
        if (!_playersInside.Contains(playerNO))
        {
            _playersInside.Add(playerNO);
            OnPlayerEntered(playerNO);
        }
    }
    
    private void CheckPlayerExit(Collider other)
    {
        if (!IsPlayer(other, out NetworkObject playerNO)) return;
        
        if (_playersInside.Contains(playerNO))
        {
            _playersInside.Remove(playerNO);
            
            if (deactivateOnExit && _playersInside.Count == 0)
            {
                OnAllPlayersExited();
            }
        }
    }
    
    private bool IsPlayer(Collider c, out NetworkObject playerNO)
    {
        playerNO = null;
        
        // วิธีที่ 1: ตรวจสอบด้วย NetworkObject.IsPlayerObject (แนะนำ)
        if (useNetworkPlayerCheck)
        {
            playerNO = c.GetComponentInParent<NetworkObject>();
            if (playerNO != null && playerNO.IsPlayerObject)
            {
                return true;
            }
        }
        
        // วิธีที่ 2: ตรวจสอบด้วย Tag
        if (!string.IsNullOrEmpty(playerTag) && c.CompareTag(playerTag))
        {
            playerNO = c.GetComponentInParent<NetworkObject>();
            if (playerNO != null)
            {
                return true;
            }
        }
        
        return false;
    }
    
    private Collider[] GetOverlaps()
    {
        Vector3 worldPos = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);
        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;
        Vector3 up = pivot ? pivot.up : Vector3.up;
        
        switch (detectionShape)
        {
            case DetectionShape.Box:
                return Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Sphere:
                return Physics.OverlapSphere(worldPos, sphereRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 a = worldPos + up * hh;
                Vector3 b = worldPos - up * hh;
                return Physics.OverlapCapsule(a, b, capsuleRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Cylinder:
                float half = cylinderHeight * 0.5f;
                Vector3 top = worldPos + up * half;
                Vector3 bottom = worldPos - up * half;
                return Physics.OverlapCapsule(top, bottom, cylinderRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            default:
                return null;
        }
    }
    #endregion
    
    #region Event Handlers
    private void OnPlayerEntered(NetworkObject playerNO)
    {
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneTrigger] Player entered: {playerNO.name}");
        }
        
        if (activateOnEnter)
        {
            if (oneTimeOnly && _hasBeenActivated)
            {
                if (showDebugLogs)
                {
                    Debug.Log($"[WindZoneTrigger] Already activated (oneTimeOnly = true)");
                }
                return;
            }
            
            if (targetManager != null)
            {
                targetManager.StartPattern();
                _hasBeenActivated = true;
                
                if (showDebugLogs)
                {
                    Debug.Log($"[WindZoneTrigger] Started WindZoneAutoManager");
                }
            }
            else
            {
                Debug.LogWarning($"[WindZoneTrigger] Target Manager is null!", this);
            }
        }
    }
    
    private void OnAllPlayersExited()
    {
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneTrigger] All players exited");
        }
        
        if (deactivateOnExit && targetManager != null)
        {
            targetManager.StopPattern();
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneTrigger] Stopped WindZoneAutoManager");
            }
        }
    }
    #endregion
    
    #region Gizmos
    private void OnDrawGizmos()
    {
        if (!showGizmo) return;
        if (!pivot) pivot = transform;
        
        Gizmos.color = gizmoColor;
        
        if (useAttachedCollider && _attachedCollider != null)
        {
            // วาด Gizmo ตาม Collider ที่แนบมา
            if (_attachedCollider is BoxCollider box)
            {
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.DrawWireCube(box.center, box.size);
            }
            else if (_attachedCollider is SphereCollider sphere)
            {
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                Gizmos.DrawSphere(sphere.center, sphere.radius);
                Gizmos.DrawWireSphere(sphere.center, sphere.radius);
            }
            else if (_attachedCollider is CapsuleCollider capsule)
            {
                Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                float hh = Mathf.Max(0, (capsule.height * 0.5f) - capsule.radius);
                Vector3 upLocal = Vector3.up * hh;
                Gizmos.DrawWireSphere(capsule.center + upLocal, capsule.radius);
                Gizmos.DrawWireSphere(capsule.center - upLocal, capsule.radius);
                Gizmos.DrawLine(capsule.center + upLocal + Vector3.forward * capsule.radius, capsule.center - upLocal + Vector3.forward * capsule.radius);
                Gizmos.DrawLine(capsule.center + upLocal - Vector3.forward * capsule.radius, capsule.center - upLocal - Vector3.forward * capsule.radius);
                Gizmos.DrawLine(capsule.center + upLocal + Vector3.right * capsule.radius, capsule.center - upLocal + Vector3.right * capsule.radius);
                Gizmos.DrawLine(capsule.center + upLocal - Vector3.right * capsule.radius, capsule.center - upLocal - Vector3.right * capsule.radius);
            }
        }
        else
        {
            // วาด Gizmo ตาม Detection Shape
            Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
            
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
                    float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                    Vector3 upLocal = Vector3.up * hh;
                    Gizmos.DrawWireSphere(detectPosition + upLocal, capsuleRadius);
                    Gizmos.DrawWireSphere(detectPosition - upLocal, capsuleRadius);
                    Gizmos.DrawLine(detectPosition + upLocal + Vector3.forward * capsuleRadius, detectPosition - upLocal + Vector3.forward * capsuleRadius);
                    Gizmos.DrawLine(detectPosition + upLocal - Vector3.forward * capsuleRadius, detectPosition - upLocal - Vector3.forward * capsuleRadius);
                    Gizmos.DrawLine(detectPosition + upLocal + Vector3.right * capsuleRadius, detectPosition - upLocal + Vector3.right * capsuleRadius);
                    Gizmos.DrawLine(detectPosition + upLocal - Vector3.right * capsuleRadius, detectPosition - upLocal - Vector3.right * capsuleRadius);
                    break;
                
                case DetectionShape.Cylinder:
                    DrawWireCylinder(detectPosition, cylinderRadius, cylinderHeight, 32);
                    break;
                
                case DetectionShape.Mesh:
                    if (meshPreview)
                    {
                        Gizmos.DrawMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);
                        Gizmos.color = new Color(0f, 0f, 0f, gizmoColor.a + 0.1f);
                        Gizmos.DrawWireMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);
                    }
                    break;
            }
        }
    }
    
    private static void DrawWireCylinder(Vector3 centerLocal, float radius, float height, int ringSegments = 24)
    {
        float half = height * 0.5f;
        Vector3 top = centerLocal + Vector3.up * half;
        Vector3 bottom = centerLocal - Vector3.up * half;
        DrawWireCircle(top, radius, ringSegments);
        DrawWireCircle(bottom, radius, ringSegments);
        int uprights = 8;
        float step = Mathf.PI * 2f / uprights;
        for (int i = 0; i < uprights; i++)
        {
            float a = i * step;
            Vector3 rim = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(bottom + rim, top + rim);
        }
    }
    
    private static void DrawWireCircle(Vector3 centerLocal, float radius, int segments = 24)
    {
        Vector3 prev = centerLocal + new Vector3(radius, 0f, 0f);
        float step = Mathf.PI * 2f / Mathf.Max(8, segments);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * step;
            Vector3 p = centerLocal + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
    #endregion
}

