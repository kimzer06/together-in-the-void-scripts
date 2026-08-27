using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Detect + Play lever tween first, then enable/disable target IActivatable (server authoritative).
/// - ใช้ตรวจจับแบบ Box/Sphere/Capsule (ตามแกน pivot)
/// - เมื่อเจอ tag ตรงตามเงื่อนไข จะยิงครั้งเดียว (one-shot) เว้นแต่ปิด "disableAfterFire"
/// - เล่นอนิเมชันคันโยก (SwitchRotator.PlayOneShot) ทั้ง Host และ Clients
/// - หน่วงเวลาตามความยาวอนิเมชัน (config) ก่อนสั่ง Activate ไปยัง targets
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DetectingSwitch : NetworkBehaviour
{
    // ---------- Targets ----------
    [Serializable]
    public class TargetEntry
    {
        [Tooltip("คอมโพเนนต์ที่ implement IActivatable")]
        public MonoBehaviour activatableComponent;

        [Tooltip("ติ๊ก = อนุญาตให้ชิ้นนี้โดนสั่ง / ไม่ติ๊ก = ข้าม")]
        public bool allow = true;

        [Tooltip("กลับค่าความจริง (true→false)")]
        public bool invert = false;
    }

    public enum PressMode { Toggle, ForceOn, ForceOff }
    private enum DetectionShape { Box, Sphere, Capsule, Mesh }

    [Header("Detect Area")]
    [SerializeField] private Transform pivot;               // ใช้กำหนดแกนหมุนของโซนตรวจจับ (ว่าง = transform)
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Vector3 boxSize = Vector3.one;
    [SerializeField] private float sphereRadius = 0.5f;
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;
    [SerializeField] private Mesh meshPreview;

    [Header("Filter")]
    [SerializeField] private LayerMask detectLayers = ~0;
    [SerializeField] private string requiredTag = "Boulder"; // Tag ของหิน (หรือวัตถุที่ต้องการให้ชนสวิตช์)

    [Header("Lever / Visual (DOTween holder)")]
    [SerializeField] private SwitchRotator modelRotator; // คอมโพเนนต์ที่เล่น DOTween (ไม่มี NetworkObject)
    [Tooltip("เวลาหน่วงก่อน Activate (ควรเท่ากับความยาว DOTween)")]
    [SerializeField, Min(0f)] private float activationDelay = 0.5f;

    [Header("Mode")]
    [SerializeField] private PressMode pressMode = PressMode.ForceOn;

    [Header("Targets (IActivatable)")]
    [SerializeField] private List<TargetEntry> targets = new();

    [Header("One-shot & Anti-spam")]
    [SerializeField] private float detectCooldown = 0.2f; // กันสัญญาณรัวๆ
    [SerializeField] private bool disableAfterFire = true; // ปิดสคริปต์หลังยิง
    private float _lastTriggerTime = -999f;
    private bool _fired;

    // runtime
    private readonly List<Rigidbody> _tracked = new();

    private void Update()
    {
        if (_fired && disableAfterFire) return;

        DetectObjects();

        // ถ้าพบวัตถุตามเงื่อนไข และผ่าน cooldown แล้ว
        if (_tracked.Count > 0 && Time.time - _lastTriggerTime > detectCooldown)
        {
            _lastTriggerTime = Time.time;
            if (IsServer) FireOnce();          // Host/Server ทำจริง
            else RequestFireServerRpc();       // Client ขอให้เซิร์ฟทำ
        }
    }

    private void DetectObjects()
    {
        if (!pivot) pivot = transform;

        Collider[] hit = null;
        Vector3 worldPos = transform.position + detectPosition;
        Quaternion worldRot = pivot.rotation;

        switch (detectionShape)
        {
            case DetectionShape.Box:
                hit = Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers);
                break;
            case DetectionShape.Sphere:
                hit = Physics.OverlapSphere(worldPos, sphereRadius, detectLayers);
                break;
            case DetectionShape.Capsule:
                {
                    float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                    Vector3 a = worldPos + pivot.up * hh;
                    Vector3 b = worldPos - pivot.up * hh;
                    hit = Physics.OverlapCapsule(a, b, capsuleRadius, detectLayers);
                }
                break;
            case DetectionShape.Mesh:
                // ต้องมี MeshCollider (convex) ถ้าจะใช้ของจริง — ที่นี่ไม่คำนวณ
                hit = Array.Empty<Collider>();
                break;
        }

        _tracked.Clear();
        if (hit == null) return;

        foreach (var c in hit)
        {
            if (!string.IsNullOrEmpty(requiredTag) && !c.CompareTag(requiredTag)) continue;
            if (c.attachedRigidbody && !_tracked.Contains(c.attachedRigidbody))
                _tracked.Add(c.attachedRigidbody);
        }
    }

    // -------- Network --------
    [ServerRpc(RequireOwnership = false)]
    private void RequestFireServerRpc() => FireOnce();

    private void FireOnce()
    {
        if (_fired && disableAfterFire) return;

        _fired = (pressMode != PressMode.Toggle); // ถ้าเป็น Toggle อนุญาตกดย้ำได้ (ไม่ล็อก)
        // 1) เล่นอนิเมชันคันโยกฝั่ง Host และบอก Clients
        PlayLeverLocal();
        PlayLeverClientRpc();

        // 2) หลังรอ DOTween เสร็จ → ค่อยสั่งเป้าหมาย
        StartCoroutine(ActivateAfterDelayCo());
    }

    private IEnumerator ActivateAfterDelayCo()
    {
        // หาก SwitchRotator ของคุณมีเมธอดคืนระยะเวลา (เช่น GetDuration) ให้เอามาใช้แทน activationDelay
        yield return new WaitForSeconds(activationDelay);

        bool nextState = ComputeNextState();
        ApplyToTargets(nextState);

        if (disableAfterFire)
            enabled = false; // กันยิงซ้ำ
    }

    // คำนวณสถานะที่จะสั่งไปยัง targets ตาม PressMode
    private bool _currentEnabledState = false; // ใช้กับ Toggle
    private bool ComputeNextState()
    {
        switch (pressMode)
        {
            case PressMode.Toggle:
                _currentEnabledState = !_currentEnabledState;
                return _currentEnabledState;
            case PressMode.ForceOn:
                _currentEnabledState = true;
                return true;
            case PressMode.ForceOff:
                _currentEnabledState = false;
                return false;
        }
        return true;
    }

    [ClientRpc]
    private void PlayLeverClientRpc()
    {
        if (IsServer) return; // Host เล่นไปแล้ว
        PlayLeverLocal();
    }

    private void PlayLeverLocal()
    {
        // เล่น DOTween ของคันโยกฝั่ง local
        modelRotator?.PlayOneShot();
    }

    private void ApplyToTargets(bool groupOn)
    {
        foreach (var t in targets)
        {
            if (!t.allow || t.activatableComponent == null) continue;

            if (t.activatableComponent is IActivatable act)
            {
                bool want = t.invert ? !groupOn : groupOn;
                act.Activate(want);
            }
            else
            {
                Debug.LogWarning($"[{name}] {t.activatableComponent.GetType().Name} ไม่ได้ implement IActivatable");
            }
        }
    }

    // -------- Gizmos --------
    private void OnDrawGizmos()
    {
        if (!pivot) pivot = transform;

        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
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
                Vector3 up = Vector3.up * hh;
                Gizmos.DrawWireSphere(detectPosition + up, capsuleRadius);
                Gizmos.DrawWireSphere(detectPosition - up, capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.forward * capsuleRadius, detectPosition - up + Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.forward * capsuleRadius, detectPosition - up - Vector3.forward * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up + Vector3.right * capsuleRadius, detectPosition - up + Vector3.right * capsuleRadius);
                Gizmos.DrawLine(detectPosition + up - Vector3.right * capsuleRadius, detectPosition - up - Vector3.right * capsuleRadius);
                break;

            case DetectionShape.Mesh:
                if (meshPreview)
                {
                    Gizmos.DrawMesh(meshPreview, detectPosition);
                    Gizmos.DrawWireMesh(meshPreview, detectPosition);
                }
                break;
        }
    }
}
