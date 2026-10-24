using StarterAssets;
using UnityEngine;

/// <summary>
/// เล็งแล้วก้ม/เงย-หันลำตัวตามจุดเล็ง (แบบ It Takes Two) — ลำตัว อก แขนที่ถือไอเทม คอ ไล่ตามทิศที่เล็ง
/// + เล็งแล้ววิ่งเฉียงหน้า/เฉียงหลัง: ขาหันตามทาง (ถอย = ท่าวิ่งเล่นย้อนจาก controller) ลำตัวท่อนบนยังหันเล็ง
///   เดินข้าง = ก้าวออก-ชิด (PlayerTurnSteps) ไม่หมุนขา
///
/// บวกมุมเพิ่ม (additive) บนท่า SkillAim ที่ Animator เล่นอยู่ ไม่ใช้ AimIK — AimIK จะ "แก้" ท่าที่วาดมาจนไอเทมชี้เป้าเป๊ะ
/// (ท่าเล็งของ Lyra บิดลำตัว ~17° / ท่ายิงมีสะบัด → โดนดัดกลับหมด) ตัวนี้แค่หมุนเพิ่มทีละกระดูก ท่าเดิมยังอยู่ครบ
/// น้ำหนักนับสะสมจากพ่อลงลูก (ลูกได้มุมของพ่อไปแล้ว): ลำตัว 0.3 + อก 0.4 + ไหล่ 0.3 = แขน/ไอเทมชี้เต็มมุม
///
/// ถอยหลัง: controller ตั้ง MotionSpeed ติดลบ (ท่าวิ่งเล่นย้อน, ซิงค์ผ่าน NetworkMecanimAnimator)
/// → หมุนสะโพก (pelvisShare) + ต้นขา (ส่วนที่เหลือ) ให้แนววิ่งย้อนตรงทางถอย ลำตัวกระดูกแรกหมุนสวนเท่าสะโพก
/// วัดจากตำแหน่ง + ค่าใน Animator — proxy คำนวณเองได้ ไม่ต้องส่งอะไรเพิ่ม
/// รันก่อน Final IK (execution order ติดลบ) — Grounder/LegIK อ่านตำแหน่งเท้าหลังหมุนขาแล้ว วางเท้าตรงที่
///
/// ภาพในเครื่องล้วน: จุดเล็งมาจาก AimedAbility_Net (เจ้าของ = เส้นเล็งจริง · เครื่องอื่น = [Networked] AimPoint)
/// Animator ตั้ง CullUpdateTransforms — นอกจอไม่เขียนกระดูกทับ → คืนท่าก่อนบวกทุก Update ไม่งั้นมุมสะสมทุกเฟรม
/// ริก Advanced Skeleton: ผูกกระดูกใต้ DeformationSystem เอง (Root_M / Hip / Spine1_M / Chest_M / Shoulder_R / คอ / หัว)
/// </summary>
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public class PlayerAimPose : MonoBehaviour
{
    [System.Serializable]
    public struct Bone
    {
        public Transform transform;
        [Tooltip("ส่วนของมุมที่กระดูกนี้หมุนเพิ่ม (สะสมกับพ่อ)")]
        [Range(0f, 1f)] public float weight;
    }

    [Header("References")]
    [Tooltip("ว่าง = หาจาก parent")]
    [SerializeField] private ThirdPersonController_Rigidbody controller;
    [Tooltip("ว่าง = หาจาก parent")]
    [SerializeField] private PlayerDeath death;
    [Tooltip("จุดที่วัดทิศไปหาเป้า (ไหล่แขนที่ถือไอเทม) — ว่าง = กระดูกตัวแรก")]
    [SerializeField] private Transform aimOrigin;

    [Header("Bones (เรียงพ่อ → ลูก)")]
    [SerializeField] private Bone[] bones = new Bone[0];

    [Header("Limits (องศา)")]
    [Tooltip("เงยได้สูงสุด — เกินนี้ไอเทมไม่ชี้ตามแล้ว")]
    [SerializeField, Range(0f, 90f)] private float maxPitchUp = 50f;
    [Tooltip("ก้มได้สูงสุด — กล้องก้มได้ถึง 70° ลำตัวงอตามหมดดูหักกลางตัว")]
    [SerializeField, Range(0f, 90f)] private float maxPitchDown = 45f;
    [Tooltip("หันซ้าย/ขวาเทียบตัว — ตัวหมุนตามกล้องอยู่แล้ว ส่วนนี้คือช่วงที่ตัวยังหันไม่ทัน / crosshair เกาะเป้าข้างๆ")]
    [SerializeField, Range(0f, 90f)] private float maxYaw = 40f;

    [Header("Feel")]
    [Tooltip("วินาทีที่มุมไล่ตามจุดเล็ง (เครื่องเจ้าของ)")]
    [SerializeField, Min(0.01f)] private float angleSmoothTime = 0.06f;
    [Tooltip("เครื่องอื่นได้จุดเล็งเป็นช่วงๆ ตาม tick — ไล่นุ่มกว่า")]
    [SerializeField, Min(0.01f)] private float proxyAngleSmoothTime = 0.12f;
    [Tooltip("ความเร็ว fade เข้า/ออก — ใกล้ skillLayerWeightLerp ของสกิล (10) ท่าเล็งกับมุมก้มมาพร้อมกัน")]
    [SerializeField, Min(0.01f)] private float fadeSpeed = 10f;

    [Header("Diagonal (เล็งแล้ววิ่งเฉียงหน้า/หลัง — ขาหันตามทาง)")]
    [Tooltip("สะโพก (Root_M) — ว่าง = ไม่หมุนสะโพก หมุนแค่ต้นขา")]
    [SerializeField] private Transform pelvis;
    [Tooltip("ส่วนของมุมขาที่สะโพกรับ (กระดูกแรกใน Bones หมุนสวนเท่านี้ ลำตัวยังหันเล็ง) — 0 = หมุนแต่ต้นขา (Bobby: ตัวโดมผูกกับสะโพก)")]
    [SerializeField, Range(0f, 1f)] private float pelvisShare = 0.3f;
    [Tooltip("ต้นขา (Hip_L / Hip_R) — หมุนส่วนที่เหลือของมุม")]
    [SerializeField] private Transform[] legs = new Transform[0];
    [Tooltip("ขาหันออกจากแนวหน้า/ถอยตรงได้สูงสุด (องศา) — เฉียงมากกว่านี้ ที่เหลือไถลเฉียงเอา")]
    [SerializeField, Range(0f, 90f)] private float maxLegYaw = 45f;
    [Tooltip("ความเร็วแนวนอน (m/s) ที่เริ่มหมุนขา / หมุนเต็มมุม")]
    [SerializeField, Min(0f)] private float strafeStartSpeed = 0.5f;
    [SerializeField, Min(0.01f)] private float strafeFullSpeed = 2f;
    [Tooltip("วินาทีที่ขาไล่ตามทิศเดิน")]
    [SerializeField, Min(0.01f)] private float legYawSmoothTime = 0.1f;

    private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");
    private static readonly int SpeedId = Animator.StringToHash("Speed");

    private AimedAbility_Net[] _abilities = new AimedAbility_Net[0];
    private Animator _animator;
    private Transform[] _managed = new Transform[0];   // ทุกกระดูกที่แตะ — จำ/คืนท่าพร้อมกัน
    private Quaternion[] _animated = new Quaternion[0];
    private bool _applied;
    private float _weight;
    private float _yaw, _pitch, _yawVel, _pitchVel;
    private float _legYaw, _legYawVel;
    private Vector3 _lastPos;
    private Vector3 _velocity;

    /// <summary>กำลังก้ม/เงยตามการเล็งอยู่ (รวมช่วง fade) — PlayerLookAt หลบให้</summary>
    public bool IsActive => _weight > 0.001f;

    private bool IsHidden => death != null && death.IsHiddenState;

    private void Awake()
    {
        if (controller == null) controller = GetComponentInParent<ThirdPersonController_Rigidbody>();
        if (death == null) death = GetComponentInParent<PlayerDeath>();
        if (controller == null || bones.Length == 0)
        {
            enabled = false;
            return;
        }
        if (aimOrigin == null) aimOrigin = bones[0].transform;
        _abilities = controller.GetComponents<AimedAbility_Net>();
        _animator = controller.GetComponent<Animator>();

        var managed = new System.Collections.Generic.List<Transform>();
        if (pelvis) managed.Add(pelvis);
        foreach (var l in legs) if (l) managed.Add(l);
        foreach (var b in bones) if (b.transform) managed.Add(b.transform);
        _managed = managed.ToArray();
        _animated = new Quaternion[_managed.Length];
    }

    private void OnEnable()
    {
        _lastPos = controller ? controller.transform.position : transform.position;
        _velocity = Vector3.zero;
    }

    private void OnDisable()
    {
        RestoreAnimatedPose();
        _weight = 0f;
        _legYaw = _legYawVel = 0f;
    }

    private void Update() => RestoreAnimatedPose();

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        TrackVelocity(dt);

        var ability = ActiveAbility();
        bool spawned = controller.Object != null && controller.Object.IsValid;
        bool active = ability != null && spawned && !IsHidden && !controller.IsGhostMode;

        float target = active ? 1f : 0f;
        _weight = Mathf.Lerp(_weight, target, 1f - Mathf.Exp(-fadeSpeed * dt));
        if (!active && _weight < 0.001f) _weight = 0f;
        if (_weight <= 0f)
        {
            _legYaw = _legYawVel = 0f;
            return;
        }

        // ปล่อยเล็งแล้ว (fade ออก) ค้างมุมสุดท้ายไว้ ลดแค่น้ำหนัก
        if (active)
        {
            MeasureAngles(ability.AimPosePoint, out float yaw, out float pitch);
            if (_weight < 0.05f)
            {
                // เริ่มเล็งใหม่ — วางมุมที่เป้าเลย ไม่ลากจากมุมของการเล็งครั้งก่อน
                _yaw = yaw;
                _pitch = pitch;
                _yawVel = _pitchVel = 0f;
            }
            else
            {
                float smooth = ability.HasStateAuthority ? angleSmoothTime : proxyAngleSmoothTime;
                _yaw = Mathf.SmoothDampAngle(_yaw, yaw, ref _yawVel, smooth);
                _pitch = Mathf.SmoothDamp(_pitch, pitch, ref _pitchVel, smooth);
            }
        }

        _legYaw = Mathf.SmoothDampAngle(_legYaw, active ? MeasureLegYaw() : 0f, ref _legYawVel, legYawSmoothTime);

        RecordAnimatedPose();
        ApplyLegYaw(_legYaw * _weight);
        ApplyAngles(_yaw, _pitch, _weight);
    }

    private AimedAbility_Net ActiveAbility()
    {
        foreach (var a in _abilities)
            if (a != null && a.AimPoseActive) return a;
        return null;
    }

    /// <summary>ความเร็วแนวนอนจากตำแหน่งจริง — proxy ไม่มี velocity ของ rigidbody ให้อ่าน</summary>
    private void TrackVelocity(float dt)
    {
        Vector3 pos = controller.transform.position;
        Vector3 d = pos - _lastPos;
        _lastPos = pos;
        d.y = 0f;
        Vector3 v = d / dt;
        if (v.sqrMagnitude > 40f * 40f) v = Vector3.zero;   // วาร์ป
        _velocity = Vector3.Lerp(_velocity, v, 1f - Mathf.Exp(-12f * dt));
    }

    /// <summary>
    /// มุมที่ขาควรหันเทียบหน้าตัว: ทางที่เดินจริง · ถอย (ท่าวิ่งเล่นย้อน) = กลับด้าน 180° ให้แนววิ่งย้อนตรงทางถอย
    /// ลอย/สไลด์/ช้า = 0 (ขาตามท่าอนิเมชั่น)
    /// </summary>
    private float MeasureLegYaw()
    {
        float speed = _velocity.magnitude;
        if (speed < strafeStartSpeed || !controller.Grounded || controller.IsSliding) return 0f;

        Vector3 fwd = controller.transform.forward;
        fwd.y = 0f;
        float a = Vector3.SignedAngle(fwd, _velocity, Vector3.up);
        // วิ่งเฉียงหน้า/เฉียงหลัง = หมุนขาตามทาง (ถอย = ท่าวิ่งเล่นย้อน → กลับด้าน 180°)
        // ก้าวข้าง (controller ตั้ง Speed → 0 ขาเป็นท่ายืน) ไม่หมุน — PlayerTurnSteps สับเท้าเอง (หมุนขา 70° ตอนเดินข้าง = ข้อเท้าบิด ผู้ใช้ตัดแล้ว)
        if (_animator != null && _animator.GetFloat(SpeedId) < 1f) return 0f;
        bool backpedal = _animator != null && _animator.GetFloat(MotionSpeedId) < 0f;
        if (backpedal) a = Mathf.DeltaAngle(0f, a + 180f);

        float k = Mathf.InverseLerp(strafeStartSpeed, strafeFullSpeed, speed);
        return Mathf.Clamp(a, -maxLegYaw, maxLegYaw) * k;
    }

    /// <summary>มุมจากไหล่ไปจุดเล็ง เทียบทิศที่ตัวหันอยู่ (วัดจากท่าอนิเมชั่น ก่อนบวกมุม)</summary>
    private void MeasureAngles(Vector3 point, out float yaw, out float pitch)
    {
        Vector3 fwd = controller.transform.forward;
        fwd.y = 0f;
        Vector3 to = point - aimOrigin.position;
        Vector3 flat = new Vector3(to.x, 0f, to.z);

        // เป้าตรงเหนือหัว/ใต้เท้าพอดี ทิศแนวนอนไม่มีความหมาย — คงมุมหันเดิม
        yaw = flat.sqrMagnitude > 0.0001f && fwd.sqrMagnitude > 0.0001f
            ? Mathf.Clamp(Vector3.SignedAngle(fwd, flat, Vector3.up), -maxYaw, maxYaw)
            : _yaw;
        pitch = Mathf.Clamp(Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg, -maxPitchDown, maxPitchUp);
    }

    /// <summary>สะโพก + ต้นขาหันไปทางเดิน · กระดูกลำตัวแรกหมุนสวนเท่าสะโพก (ท่อนบนยังหันเล็ง)</summary>
    private void ApplyLegYaw(float yaw)
    {
        if (Mathf.Abs(yaw) < 0.01f) return;

        float pelvisYaw = pelvis ? yaw * pelvisShare : 0f;
        if (pelvis) pelvis.rotation = Quaternion.AngleAxis(pelvisYaw, Vector3.up) * pelvis.rotation;

        Quaternion legRot = Quaternion.AngleAxis(yaw - pelvisYaw, Vector3.up);
        foreach (var l in legs)
            if (l) l.rotation = legRot * l.rotation;

        var spine = bones[0].transform;
        if (spine && pelvisYaw != 0f) spine.rotation = Quaternion.AngleAxis(-pelvisYaw, Vector3.up) * spine.rotation;
    }

    /// <summary>
    /// หมุนเพิ่มทีละกระดูก (พ่อก่อนลูก) — เงย = หมุนรอบแกนขวาของตัวทางลบ, หัน = รอบแกนตั้ง
    /// ทุกกระดูกหมุนรอบแกนเดียวกัน มุมที่ลูกได้ = ผลรวมน้ำหนักของสายพ่อ
    /// </summary>
    private void ApplyAngles(float yaw, float pitch, float weight)
    {
        Vector3 fwd = controller.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;
        Vector3 right = Vector3.Cross(Vector3.up, fwd.normalized);
        Quaternion full = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(-pitch, right);

        for (int i = 0; i < bones.Length; i++)
        {
            var t = bones[i].transform;
            if (!t) continue;
            t.rotation = Quaternion.Slerp(Quaternion.identity, full, bones[i].weight * weight) * t.rotation;
        }
    }

    private void RecordAnimatedPose()
    {
        for (int i = 0; i < _managed.Length; i++) _animated[i] = _managed[i].localRotation;
        _applied = true;
    }

    private void RestoreAnimatedPose()
    {
        if (!_applied) return;
        for (int i = 0; i < _managed.Length; i++)
            if (_managed[i]) _managed[i].localRotation = _animated[i];
        _applied = false;
    }
}
