using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class AscendAbility_Local : MonoBehaviour
{
    [Header("Refs (ปล่อยว่างได้ เดี๋ยวหา MainCamera ให้)")]
    [SerializeField] private Camera playerCam;

    [Header("Layers")]
    [Tooltip("ตั้งเลเยอร์ Ascendable ให้กับวัตถุที่อนุญาตให้มุดผ่าน")]
    [SerializeField] private LayerMask ascendableMask;

    [Header("Aim / Cast")]
    [Tooltip("ออฟเซ็ตจากจุดศูนย์กลางขึ้นไปเป็นตำแหน่งหัว (เมตร)")]
    [SerializeField] private float headCastOffset = 0.9f;
    [Tooltip("ระยะ Raycast สูงสุดขึ้นไปบนหัว (เมตร)")]
    [SerializeField] private float aimMaxDistance = 12f;

    [Header("Ascend Motion")]
    [Tooltip("ความเร็วเลื่อนขึ้น (m/s)")]
    [SerializeField] private float ascendSpeed = 6f;
    [Tooltip("ให้เท้าพ้นเหนือยอดอย่างน้อยเท่านี้ (เมตร)")]
    [SerializeField] private float feetClearance = 0.05f;
    [Tooltip("กันจมพื้น เพิ่มอีกนิดหลังคำนวณระดับเท้า")]
    [SerializeField] private float topSnapExtra = 0.02f;

    [Header("During Ascend")]
    [Tooltip("ระหว่างมุด ปิด CapsuleCollider ชั่วคราว")]
    [SerializeField] private bool disableCapsuleDuringAscend = true;
    [Tooltip("ตัดความเร็วค้างก่อนเริ่มมุด")]
    [SerializeField] private bool zeroVelocityOnBegin = true;
    [Tooltip("ปิดคอนโทรลเลอร์เดินของคุณ (ถ้ามี) ระหว่างมุด")]
    [SerializeField] private bool disableMoveControllerDuringAscend = true;

    [Header("Aim VFX (ใส่เป็น 'Prefab' แล้วสคริปต์จะ Instantiate เอง)")]
    [Tooltip("พรีแฟบของพาร์ติเคิลที่ใช้แสดงตอนเล็งเจอ Ascendable")]
    [SerializeField] private ParticleSystem aimHitParticlePrefab;
    [Tooltip("Anchor ที่อยู่ 'ใน Prefab ของ Player' ก็ได้ เดี๋ยวสคริปต์จะรีโซลฟ์เป็นอินสแตนซ์ในซีนให้")]
    [SerializeField] private Transform particleAnchor;
    [Tooltip("Path ถึงลูกภายใต้ Player (เช่น \"VFX/AscendAnchor\" หรือชื่อวัตถุ \"AscendAnchor\")")]
    [SerializeField] private string anchorChildPath = "AscendAnchor";
    [Tooltip("ออฟเซ็ตจากตำแหน่งของ anchor")]
    [SerializeField] private Vector3 particleOffset = Vector3.zero;

    [Header("Gizmos (ช่วยเล็งภาพ)")]
    [SerializeField] private bool drawGizmosWhileAiming = true;
    [SerializeField] private float gizmoRadius = 0.05f;

    // ---- runtime ----
    private Rigidbody _rb;
    private CapsuleCollider _capsule;
    private StarterAssets.ThirdPersonController_Rigidbody _moveController; // ถ้ามีจะถูกปิด/เปิดชั่วคราว

    private bool _isAiming;
    private bool _isAscending;
    private Collider _currentHitCollider;
    private Vector3 _currentHitPoint;

    // อินสแตนซ์พาร์ติเคิลที่สร้างตอนรันไทม์
    private ParticleSystem _aimFX;

#if ENABLE_INPUT_SYSTEM
    private bool RightHeld => Mouse.current != null && Mouse.current.rightButton.isPressed;
    private bool LeftDown  => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
    private bool RightHeld => Input.GetMouseButton(1);
    private bool LeftDown => Input.GetMouseButtonDown(0);
#endif

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _moveController = GetComponent<StarterAssets.ThirdPersonController_Rigidbody>();
    }

    private void Start()
    {
        if (playerCam == null)
        {
            var camGo = GameObject.FindGameObjectWithTag("MainCamera");
            if (camGo) playerCam = camGo.GetComponent<Camera>();
        }

        // 🧭 รีโซลฟ์ anchor ให้เป็นอินสแตนซ์ในซีน (แม้จะอ้างจาก Prefab ของ Player มาก็ตาม)
        particleAnchor = ResolveAnchorInstance(particleAnchor, transform, anchorChildPath);

        // สร้างอินสแตนซ์พาร์ติเคิลจาก Prefab
        if (aimHitParticlePrefab != null)
        {
            _aimFX = Instantiate(aimHitParticlePrefab, transform.position, Quaternion.identity);
            ConfigureAimParticle(_aimFX);
            _aimFX.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    private void OnDisable()
    {
        StopAimParticle();
    }

    private void Update()
    {
        HandleAimAndInput();
    }

    private void HandleAimAndInput()
    {
        _isAiming = RightHeld && !_isAscending;

        if (_isAiming)
        {
            Vector3 headPos = transform.position + Vector3.up * headCastOffset;

            if (Physics.Raycast(headPos, Vector3.up, out RaycastHit hit, aimMaxDistance, ascendableMask, QueryTriggerInteraction.Ignore))
            {
                _currentHitCollider = hit.collider;
                _currentHitPoint = hit.point;

                // แสดงพาร์ติเคิลที่ตำแหน่ง anchor (อัปเดตทุกเฟรม; ไม่แตะ Prefab Asset)
                ShowAimParticleAtAnchor();

                if (LeftDown)
                {
                    BeginAscendFromHit(_currentHitCollider);
                }
            }
            else
            {
                _currentHitCollider = null;
                StopAimParticle();
            }
        }
        else
        {
            _currentHitCollider = null;
            StopAimParticle();
        }
    }

    // ---------- Ascend flow ----------
    private void BeginAscendFromHit(Collider ascendable)
    {
        if (_isAscending || ascendable == null) return;

        Bounds b = ascendable.bounds;
        float topY = b.max.y;

        // ระยะจาก pivot ลงไปถึงเท้า
        float feetToPivot = (_capsule.height * 0.5f) - _capsule.radius;
        // ให้เท้าพ้นยอด
        float targetFeetY = topY + feetClearance;
        float targetPivotY = targetFeetY + feetToPivot + topSnapExtra;

        Vector3 targetPivotPos = new Vector3(transform.position.x, targetPivotY, transform.position.z);

        _isAscending = true;
        StopAimParticle();
        SetLocalMovementEnabled(false);

        StartCoroutine(AscendRoutineLocal(targetPivotPos, topY + 0.001f));
    }

    private System.Collections.IEnumerator AscendRoutineLocal(Vector3 targetPivotPos, float topYForSafety)
    {
        // ปิดการชน/ฟิสิกส์ชั่วคราว
        bool prevDetect = _rb.detectCollisions;
        bool prevKinematic = _rb.isKinematic;
        bool prevCapsule = _capsule.enabled;

        if (zeroVelocityOnBegin) _rb.linearVelocity = Vector3.zero;
        _rb.isKinematic = true;
        _rb.detectCollisions = false;
        if (disableCapsuleDuringAscend) _capsule.enabled = false;

        // เคลื่อนที่นิ่ม ๆ
        Vector3 start = transform.position;
        float dist = Vector3.Distance(start, targetPivotPos);
        float duration = Mathf.Max(0.01f, dist / Mathf.Max(0.01f, ascendSpeed));
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            transform.position = Vector3.Lerp(start, targetPivotPos, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }
        transform.position = targetPivotPos;

        // ให้ “เท้า” พ้นยอดแน่ ๆ
        float feetToPivot = (_capsule.height * 0.5f) - _capsule.radius;
        float feetY = transform.position.y - feetToPivot;
        if (feetY < topYForSafety)
        {
            float delta = (topYForSafety - feetY) + feetClearance;
            transform.position += Vector3.up * delta;
        }

        // เปิดกลับ
        _rb.detectCollisions = prevDetect;
        _rb.isKinematic = prevKinematic;
        _capsule.enabled = prevCapsule;

        SetLocalMovementEnabled(true);
        _isAscending = false;
    }

    // ---------- Helpers ----------
    private void SetLocalMovementEnabled(bool enabled)
    {
        if (disableMoveControllerDuringAscend && _moveController)
            _moveController.enabled = enabled;
    }

    private static void ConfigureAimParticle(ParticleSystem ps)
    {
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.enabled = true;
    }

    private void ShowAimParticleAtAnchor()
    {
        if (_aimFX == null) return;

        // ใช้ anchor อินสแตนซ์ (ถ้าไม่มีให้ยึดตำแหน่งผู้เล่น)
        Vector3 pos = (particleAnchor != null && particleAnchor.gameObject.scene.IsValid())
                      ? particleAnchor.position + particleOffset
                      : transform.position + particleOffset;

        _aimFX.transform.position = pos;

        if (!_aimFX.gameObject.activeSelf) _aimFX.gameObject.SetActive(true);
        if (!_aimFX.isPlaying) _aimFX.Play(true);

        // ถ้า rateOverTime = 0 ยิง burst เล็ก ๆ เพื่อให้เห็นทันที
        var emission = _aimFX.emission;
        if (emission.enabled && emission.rateOverTime.constant <= 0f)
            _aimFX.Emit(1);
    }

    private void StopAimParticle()
    {
        if (_aimFX == null) return;
        _aimFX.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    // ---------- Anchor resolve (จาก Prefab → อินสแตนซ์ในซีน) ----------
    private Transform ResolveAnchorInstance(Transform anchor, Transform root, string childPath)
    {
        // ถ้า anchor ที่เซ็ตไว้ เป็นอินสแตนซ์ในซีนอยู่แล้ว → ใช้ได้เลย
        if (anchor != null && anchor.gameObject.scene.IsValid())
            return anchor;

        // ลองหาโดย path ใต้ root ของ Player (เช่น "VFX/AscendAnchor")
        if (!string.IsNullOrEmpty(childPath))
        {
            var found = root.Find(childPath);
            if (found != null) return found;
        }

        // หาแบบ recursive โดยชื่อ (ใช้ส่วนท้ายของ path เป็นชื่อ)
        if (!string.IsNullOrEmpty(childPath))
        {
            string nameOnly = childPath.Contains("/")
                ? childPath.Substring(childPath.LastIndexOf('/') + 1)
                : childPath;
            return FindChildRecursiveByName(root, nameOnly);
        }
        return null;
    }

    private Transform FindChildRecursiveByName(Transform parent, string name)
    {
        foreach (Transform c in parent)
        {
            if (c.name == name) return c;
            var sub = FindChildRecursiveByName(c, name);
            if (sub != null) return sub;
        }
        return null;
    }

    // ---------- Gizmos ----------
    private void OnDrawGizmos()
    {
        if (!drawGizmosWhileAiming) return;

        if (_isAiming)
        {
            Gizmos.color = Color.cyan;
            Vector3 headPos = transform.position + Vector3.up * headCastOffset;
            Gizmos.DrawLine(headPos, headPos + Vector3.up * aimMaxDistance);
        }

        if (_currentHitCollider)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(_currentHitPoint, gizmoRadius);
        }
    }
}