using UnityEngine;
using UnityEngine.Rendering.Universal;
using Unity.Netcode;

/// <summary>
/// วางสคริปต์นี้บน Collider ที่ต้องการให้พอร์ทัล "snap" ไปยัง spawnPoint ที่กำหนด
/// เมื่อพอร์ทัลสกิลยิงโดน Collider นี้ จะวางพอร์ทัลที่ตำแหน่งและทิศทางของ spawnPoint แทน
/// นอกจากนี้ยังเร่งความเข้ม Emission ของ URP Decal Projector เมื่อถูกยิง
/// </summary>
public class PortalSnapPoint : MonoBehaviour
{
    [Header("Portal Spawn Point")]
    [Tooltip("ตำแหน่งและทิศทางของพอร์ทัลที่จะ spawn (ใช้ Transform.position และ Transform.forward)")]
    [SerializeField] private Transform spawnPoint;

    [Header("Despawn Option")]
    [Tooltip("ถ้าตั้งเป็น true เมื่อ Object นี้ขยับ จะทำลายพอร์ทัลที่ snap อยู่นี้ทิ้งทันที")]
    [SerializeField] private bool despawnOnMove = false;

    // ────────────────────────────────────────────────────────────
    //  Decal Emission Boost  —  เร่ง emission เมื่อโดนยิง
    // ────────────────────────────────────────────────────────────
    [Header("Decal Emission Boost")]
    [Tooltip("URP Decal Projector ทั้งหมดที่ต้องการเร่ง emission เมื่อถูกยิง (ใส่ได้หลายชิ้น)")]
    [SerializeField] private DecalProjector[] decalProjectors;

    [Tooltip("ค่า emission intensity ปกติ (ก่อนถูกยิง)")]
    [SerializeField, Min(0f)] private float baseEmissionIntensity = 1f;

    [Tooltip("ค่า emission intensity สูงสุดเมื่อถูกยิง")]
    [SerializeField, Min(0f)] private float boostEmissionIntensity = 3f;

    [Tooltip("ระยะเวลาค้างที่ความเข้มสูงสุด (วินาที)")]
    [SerializeField, Min(0f)] private float boostHoldDuration = 0.5f;

    [Tooltip("ระยะเวลาที่ค่อย ๆ ลดลงกลับสู่ปกติ (วินาที)")]
    [SerializeField, Min(0.01f)] private float fadeDuration = 0.8f;

    [Tooltip("Curve สำหรับการ fade กลับ (0 = ค่า boost, 1 = ค่าปกติ)")]
    [SerializeField] private AnimationCurve fadeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("ชื่อ property ของ Emission ใน Shader Graph (ดูได้จาก Reference ใน Blackboard ของ Shader Graph)")]
    [SerializeField] private string emissionPropertyName = "_Emission";

    // ────────────────────────────────────────────────────────────
    //  Breathing Pulse  —  กระพริบแบบลมหายใจเมื่อมี portal เปิดอยู่
    // ────────────────────────────────────────────────────────────
    [Header("Breathing Pulse (เมื่อ Portal เปิดอยู่)")]
    [Tooltip("เปิด/ปิดเอฟเฟกต์กระพริบแบบลมหายใจเมื่อมีพอร์ทัลอยู่ที่จุดนี้")]
    [SerializeField] private bool enableBreathingPulse = true;

    [Tooltip("ค่า emission ต่ำสุดของจังหวะลมหายใจ")]
    [SerializeField, Min(0f)] private float breathMinIntensity = 0.5f;

    [Tooltip("ค่า emission สูงสุดของจังหวะลมหายใจ")]
    [SerializeField, Min(0f)] private float breathMaxIntensity = 2.5f;

    [Tooltip("ความเร็วของจังหวะลมหายใจ (รอบต่อวินาที)")]
    [SerializeField, Min(0.01f)] private float breathSpeed = 0.6f;

    // ข้อมูลต่อ Decal แต่ละตัว
    private struct DecalData
    {
        public Material matInstance;
        public int emissionPropId;
        public Color baseEmissionColor;
    }

    private DecalData[] _decals;
    private bool _isBoosting;
    private float _boostTimer;
    private bool _isBreathing;
    private float _breathTimer;

    /// <summary>
    /// ตำแหน่งที่พอร์ทัลจะ spawn
    /// </summary>
    public Vector3 SpawnPosition => spawnPoint != null ? spawnPoint.position : transform.position;

    /// <summary>
    /// การหมุนของพอร์ทัล (พอร์ทัลจะหันหน้าไปทาง forward ของ spawnPoint)
    /// </summary>
    public Quaternion SpawnRotation => spawnPoint != null ? spawnPoint.rotation : transform.rotation;

    /// <summary>
    /// มี spawnPoint กำหนดหรือไม่ (ถ้าไม่มี พอร์ทัลจะ spawn ตรงจุดที่กระสุนชนแทน)
    /// </summary>
    public bool HasSpawnPoint => spawnPoint != null;

    private Vector3 _lastPos;
    private bool _hadPortalA;
    private bool _hadPortalB;

    private void Start()
    {
        _lastPos = SpawnPosition;
        InitDecalMaterials();
    }

    /// <summary>
    /// เรียกจาก PortalProjectile เมื่อยิงโดน PortalSnapPoint นี้
    /// จะเร่ง emission ของ Decal Projector ทันที
    /// </summary>
    public void OnHitByProjectile()
    {
        if (_decals == null || _decals.Length == 0) return;

        _isBoosting = true;
        _boostTimer = 0f;
        ApplyEmissionIntensity(boostEmissionIntensity);
    }

    private void Update()
    {
        UpdateDespawnLogic();
        UpdateEmissionBoost();
        UpdateBreathingPulse();
    }

    // ────────────────────────────────────────────────────────────
    //  Emission helpers
    // ────────────────────────────────────────────────────────────

    private void InitDecalMaterials()
    {
        if (decalProjectors == null || decalProjectors.Length == 0) return;

        // นับเฉพาะตัวที่ไม่ null
        int validCount = 0;
        foreach (var dp in decalProjectors)
            if (dp != null) validCount++;

        if (validCount == 0) return;

        _decals = new DecalData[validCount];
        int idx = 0;

        foreach (var dp in decalProjectors)
        {
            if (dp == null) continue;

            // สร้าง material instance เพื่อไม่ให้กระทบ Decal อื่น ๆ
            var matInst = new Material(dp.material);
            dp.material = matInst;

            int propId = ResolveEmissionPropertyId(matInst);

            Color baseCol = Color.white;
            if (propId != 0)
            {
                baseCol = matInst.GetColor(propId);
                if (baseCol == Color.black)
                    baseCol = Color.white;
            }

            _decals[idx++] = new DecalData
            {
                matInstance = matInst,
                emissionPropId = propId,
                baseEmissionColor = baseCol,
            };
        }

        ApplyEmissionIntensity(baseEmissionIntensity);
    }

    /// <summary>
    /// หา property ID ของ emission จาก material
    /// ลองชื่อที่ user กำหนดก่อน แล้ว fallback ไปชื่อมาตรฐาน
    /// </summary>
    private int ResolveEmissionPropertyId(Material mat)
    {
        // 1) ชื่อที่ user กำหนดเอง (จาก Shader Graph)
        if (!string.IsNullOrEmpty(emissionPropertyName))
        {
            int custom = Shader.PropertyToID(emissionPropertyName);
            if (mat.HasProperty(custom)) return custom;
        }
        // 2) fallback: ชื่อมาตรฐาน URP Decal
        int emissive = Shader.PropertyToID("_EmissiveColor");
        if (mat.HasProperty(emissive)) return emissive;
        // 3) fallback: ชื่อมาตรฐาน Lit
        int emission = Shader.PropertyToID("_EmissionColor");
        if (mat.HasProperty(emission)) return emission;

        Debug.LogWarning($"[PortalSnapPoint] ไม่เจอ emission property '{emissionPropertyName}' ใน material '{mat.name}'. " +
                         $"กรุณาเช็คชื่อ Reference ใน Shader Graph Blackboard.", this);
        return 0;
    }

    private void ApplyEmissionIntensity(float intensity)
    {
        if (_decals == null) return;

        for (int i = 0; i < _decals.Length; i++)
        {
            ref var d = ref _decals[i];
            if (d.matInstance == null || d.emissionPropId == 0) continue;
            Color hdrColor = d.baseEmissionColor * intensity;
            d.matInstance.SetColor(d.emissionPropId, hdrColor);
        }
    }

    private void UpdateEmissionBoost()
    {
        if (!_isBoosting || _decals == null) return;

        _boostTimer += Time.deltaTime;

        float totalDuration = boostHoldDuration + fadeDuration;

        if (_boostTimer >= totalDuration)
        {
            // จบ boost → กลับเป็นค่าปกติ
            ApplyEmissionIntensity(baseEmissionIntensity);
            _isBoosting = false;
            return;
        }

        if (_boostTimer <= boostHoldDuration)
        {
            // ยังอยู่ในช่วง hold → คงค่า boost
            ApplyEmissionIntensity(boostEmissionIntensity);
        }
        else
        {
            // ช่วง fade → ค่อย ๆ ลดจาก boost → base
            float fadeT = (_boostTimer - boostHoldDuration) / fadeDuration;
            float curveT = fadeCurve.Evaluate(fadeT);
            float intensity = Mathf.Lerp(boostEmissionIntensity, baseEmissionIntensity, curveT);
            ApplyEmissionIntensity(intensity);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Breathing Pulse logic
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ตรวจว่ามี portal อยู่ที่ snap point นี้หรือไม่
    /// </summary>
    private bool HasPortalAtThisPoint()
    {
        if (PortalPairManager_Net.Instance == null) return false;

        if (PortalPairManager_Net.Instance.TryGetPortal(true, out var pa))
        {
            if (Vector3.Distance(pa.transform.position, SpawnPosition) <= 0.05f)
                return true;
        }

        if (PortalPairManager_Net.Instance.TryGetPortal(false, out var pb))
        {
            if (Vector3.Distance(pb.transform.position, SpawnPosition) <= 0.05f)
                return true;
        }

        return false;
    }

    private void UpdateBreathingPulse()
    {
        if (!enableBreathingPulse || _decals == null) return;

        bool shouldBreathe = HasPortalAtThisPoint();

        if (shouldBreathe)
        {
            // เริ่ม breathing ถ้ายังไม่ได้เริ่ม
            if (!_isBreathing)
            {
                _isBreathing = true;
                _breathTimer = 0f;
            }

            // ไม่ override ขณะ boost กำลังเล่นอยู่ (ให้ boost จบก่อน)
            if (_isBoosting) return;

            _breathTimer += Time.deltaTime;

            // ใช้ sine wave เพื่อให้ได้จังหวะลมหายใจ (smooth 0→1→0)
            // sin ได้ค่า -1 ถึง 1  →  remap เป็น 0 ถึง 1
            float t = (Mathf.Sin(_breathTimer * breathSpeed * Mathf.PI * 2f - Mathf.PI * 0.5f) + 1f) * 0.5f;
            float intensity = Mathf.Lerp(breathMinIntensity, breathMaxIntensity, t);
            ApplyEmissionIntensity(intensity);
        }
        else
        {
            // portal หายแล้ว → กลับสู่ base
            if (_isBreathing)
            {
                _isBreathing = false;
                if (!_isBoosting)
                    ApplyEmissionIntensity(baseEmissionIntensity);
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Despawn-on-move logic (เดิม)
    // ────────────────────────────────────────────────────────────

    private void UpdateDespawnLogic()
    {
        if (!despawnOnMove) return;

        // ถ้ายังไม่มี PortalPairManager ข้ามไปก่อน
        if (PortalPairManager_Net.Instance == null) return;

        bool hasA = false;
        if (PortalPairManager_Net.Instance.TryGetPortal(true, out var pa))
        {
            if (Vector3.Distance(pa.transform.position, _lastPos) <= 0.05f) hasA = true;
        }

        bool hasB = false;
        if (PortalPairManager_Net.Instance.TryGetPortal(false, out var pb))
        {
            if (Vector3.Distance(pb.transform.position, _lastPos) <= 0.05f) hasB = true;
        }

        // ตรวจสอบว่าตําแหน่ง SpawnPosition เปลี่ยนไปหรือไม่
        if (Vector3.Distance(_lastPos, SpawnPosition) > 0.001f)
        {
            if (_hadPortalA || _hadPortalB || hasA || hasB)
            {
                // สั่งทำลาย Portal (ให้ Server เป็นคนสั่ง) พร้อมเล่น Particle บนทุก Client
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                {
                    if (_hadPortalA || hasA) PortalPairManager_Net.Instance.ForceDespawnPortal(true);
                    if (_hadPortalB || hasB) PortalPairManager_Net.Instance.ForceDespawnPortal(false);
                }
            }
            
            _lastPos = SpawnPosition;
            hasA = false;
            hasB = false;
        }

        _hadPortalA = hasA;
        _hadPortalB = hasB;
    }

    private void OnDestroy()
    {
        // ทำลาย material instance ทั้งหมดเพื่อป้องกัน memory leak
        if (_decals == null) return;
        foreach (var d in _decals)
        {
            if (d.matInstance != null)
                Destroy(d.matInstance);
        }
    }

#if UNITY_EDITOR
    [Header("Debug / Gizmos")]
    [SerializeField] private Color gizmoColor = Color.cyan;
    [SerializeField] private float gizmoSize = 0.3f;
    [SerializeField] private float arrowLength = 1f;

    private void OnDrawGizmosSelected()
    {
        if (spawnPoint == null) return;

        Gizmos.color = gizmoColor;
        // วาดจุด spawn
        Gizmos.DrawWireSphere(spawnPoint.position, gizmoSize);

        // วาดลูกศร forward (ทิศทางหน้าพอร์ทัล)
        Gizmos.DrawRay(spawnPoint.position, spawnPoint.forward * arrowLength);

        // วาดเส้นจาก collider ไปยัง spawn point
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.5f);
        Gizmos.DrawLine(transform.position, spawnPoint.position);
    }
#endif
}
