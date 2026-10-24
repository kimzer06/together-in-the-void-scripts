using RootMotion.FinalIK;
using StarterAssets;
using UnityEngine;

/// <summary>
/// เท้าเหยียบพื้นต่างระดับ (Final IK: GrounderIK + LegIK สองขา) — สคริปต์นี้คุมแค่ "เปิดเมื่อไหร่" การวางเท้า/ลดสะโพกเป็นงานของ Grounder
/// ภาพในเครื่องล้วน ไม่มี network: ทุกเครื่อง (รวม proxy) raycast หาพื้นเอง · Grounded มาจาก GroundedCheck ที่รันทุกเครื่องอยู่แล้ว
///
/// fade weight → 0 ตอน: ลอยกลางอากาศ / Ghost / ตาย-ซ่อนตัว · สไลด์ = เปิดด้วย slideWeight (ปิด prediction)
/// วาร์ป (respawn / portal / checkpoint) → ResetPosition() ไม่งั้น Grounder คิดว่าเท้าวิ่งเร็วมาก (prediction ตามความเร็ว) ขาเหวี่ยงไปหลายเฟรม
/// layer ใช้ GroundLayers ของ controller ชุดเดียวกัน — เท้าเหยียบเฉพาะของที่ตัวละครยืนได้จริง
///
/// ริก Advanced Skeleton: LegIK ต้องผูก Root_M / Hip / Knee / Ankle / Toes ใต้ DeformationSystem เอง (อย่าใช้ auto-detect)
/// </summary>
[DisallowMultipleComponent]
public class PlayerFootIK : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GrounderIK grounder;
    [Tooltip("ว่าง = หาจาก parent")]
    [SerializeField] private ThirdPersonController_Rigidbody controller;
    [Tooltip("ว่าง = หาจาก parent")]
    [SerializeField] private PlayerDeath death;

    [Header("Weight")]
    [Tooltip("ความเร็ว fade เข้า (ต่อวินาที) — 1/ค่านี้ = วินาทีจนเต็ม")]
    [SerializeField, Min(0.01f)] private float fadeInSpeed = 5f;
    [Tooltip("ความเร็ว fade ออก (ต่อวินาที) — เร็วกว่าขาเข้า ขาไม่ค้างงอตอนกระโดด")]
    [SerializeField, Min(0.01f)] private float fadeOutSpeed = 12f;

    [Header("Slide")]
    [Tooltip("เปิด Foot IK ตอนสไลด์ (เท้าไม่จมผิวทางตอนลำตัวเอียง/ท่าสไลด์)")]
    [SerializeField] private bool activeWhileSliding = true;
    [Tooltip("weight สูงสุดตอนสไลด์")]
    [SerializeField, Range(0f, 1f)] private float slideWeight = 1f;

    [Header("Run")]
    [Tooltip("footSpeed ของ Grounder ตอนวิ่งเต็มที่ (ม./วิ ที่เป้าเท้าไล่ตามความสูงพื้น) — เดิน/ยืนใช้ค่าใน Grounder\n" +
             "วิ่งลงทางลาด/ข้ามรอยต่อ พื้นใต้เท้าที่แกว่งหน้า-หลังเปลี่ยนเร็ว ค่าเดิม 3 (= 5 ซม./เฟรม) ไล่ตามทันทุกเฟรม ขากระตุก")]
    [SerializeField, Min(0.1f)] private float runFootSpeed = 1f;
    [Tooltip("ความเร็วแนวนอน (m/s): ต่ำกว่า x = footSpeed เดิม · สูงกว่า y = runFootSpeed")]
    [SerializeField] private Vector2 runSpeedRange = new Vector2(2f, 6f);

    [Header("Knee Direction")]
    [Tooltip("ท่าอนิเมชั่นเข่าเหยียดเกินมุมนี้ (องศา) = ใช้ทิศเข่าของเฟรมล่าสุดที่ยังงออยู่แทน\n" +
             "ขาตรง = Cross(ต้นขา, หน้าแข้ง) ≈ 0 ทิศเข่าออกมาสุ่ม → IK งอเข่าลงวางเท้า เข่าพับออกข้าง ~78° 1–2 เฟรม (Lyra ท่าวิ่งเหยียด 176–180°)")]
    [SerializeField, Range(150f, 179f)] private float straightKneeAngle = 170f;

    [Header("Misc")]
    [Tooltip("ตัวขยับเกินระยะนี้ในเฟรมเดียว = วาร์ป → รีเซ็ต Grounder")]
    [SerializeField, Min(0.1f)] private float teleportDistance = 1.5f;
    [Tooltip("ใช้ GroundLayers ของ controller แทน Layers ใน Grounder")]
    [SerializeField] private bool useControllerGroundLayers = true;

    private Vector3 _lastPos;
    private bool _wasHidden;
    private float _basePrediction;
    private float _baseFootSpeed;
    private float _speed;   // ความเร็วแนวนอนจาก transform (กรองแล้ว) — proxy ไม่มี rigidbody velocity
    private LegIK[] _legIKs;
    private Vector3[] _lastBendRelToPelvis;   // ทิศเข่า (เทียบ pelvis) ของเฟรมล่าสุดที่ท่ายังงอ

    private void Awake()
    {
        if (controller == null) controller = GetComponentInParent<ThirdPersonController_Rigidbody>();
        if (death == null) death = GetComponentInParent<PlayerDeath>();
        if (grounder == null || controller == null)
        {
            enabled = false;
            return;
        }

        if (useControllerGroundLayers) grounder.solver.layers = controller.GroundLayers;
        grounder.weight = 0f;
        _basePrediction = grounder.solver.prediction;
        _baseFootSpeed = grounder.solver.footSpeed;

        // ทิศเข่าตามท่าอนิเมชั่นทุกเฟรม (ค่า default ของ LegIK = ทิศจากท่า prefab ตอนเริ่ม + ทิศปลายเท้า)
        // ไม่งั้นท่าที่ขาไม่ชี้ตรงหน้า (แดช/เลี้ยว) เข่าถูกบังคับเข้าระนาบผิด บิดได้ถึง ~25° แม้ยืนบนพื้นเรียบ
        // field นี้ HideInInspector ใน Final IK เลยตั้งในโค้ด
        // ยกเว้นตอนท่าเหยียดขาตรง — ดู StabilizeKneeDirection
        _legIKs = new LegIK[grounder.legs.Length];
        _lastBendRelToPelvis = new Vector3[grounder.legs.Length];
        for (int i = 0; i < grounder.legs.Length; i++)
        {
            if (!(grounder.legs[i] is LegIK legIK)) continue;
            _legIKs[i] = legIK;
            legIK.solver.leg.useAnimatedBendNormal = true;
            // ทางที่ไม่ใช้ท่าอนิเมชั่น = ทิศที่เราเก็บไว้ (เทียบ pelvis) ล้วนๆ ไม่ผสมทิศตามเท้า
            legIK.solver.leg.bendToTargetWeight = 0f;
        }

        SetLegsEnabled(false);
    }

    private void OnEnable()
    {
        _lastPos = transform.position;
        if (grounder) grounder.OnPreGrounder += StabilizeKneeDirection;
    }

    private void OnDisable()
    {
        if (grounder)
        {
            grounder.OnPreGrounder -= StabilizeKneeDirection;
            grounder.weight = 0f;
        }
        SetLegsEnabled(false);
    }

    /// <summary>
    /// ก่อน Grounder แก้ท่า (กระดูกยังเป็นท่าอนิเมชั่น): ขาในท่างอพอ → ใช้ทิศเข่าจากท่าแล้วจำไว้ ·
    /// ขาเกือบตรง → ปิด useAnimatedBendNormal แล้วป้อนทิศที่จำไว้ (LegIK หมุนตาม pelvis ให้เอง)
    /// ทางเลือกตอนขาตรงไม่สำคัญกับขาที่ตรงอยู่ — สำคัญตอน IK ต้องงอเข่าลงวางเท้า ให้พับไปหน้าแบบเฟรมก่อน ไม่ใช่ทิศสุ่ม
    /// </summary>
    private void StabilizeKneeDirection()
    {
        if (_legIKs == null) return;
        float minSin = Mathf.Sin((180f - straightKneeAngle) * Mathf.Deg2Rad);

        for (int i = 0; i < _legIKs.Length; i++)
        {
            var legIK = _legIKs[i];
            if (legIK == null) continue;
            var s = legIK.solver;
            if (s.thigh.transform == null || s.calf.transform == null || s.foot.transform == null || s.pelvis.transform == null) continue;

            Vector3 thigh = s.thigh.transform.position;
            Vector3 calf = s.calf.transform.position;
            Vector3 foot = s.foot.transform.position;
            Vector3 a = calf - thigh, b = foot - calf;
            Vector3 n = Vector3.Cross(a, b);
            float denom = a.magnitude * b.magnitude;
            bool bent = denom > 1e-6f && n.magnitude / denom > minSin;

            if (bent)
            {
                _lastBendRelToPelvis[i] = Quaternion.Inverse(s.pelvis.transform.rotation) * n.normalized;
                s.leg.useAnimatedBendNormal = true;
            }
            else if (_lastBendRelToPelvis[i] != Vector3.zero)
            {
                s.leg.bendNormalRelToPelvis = _lastBendRelToPelvis[i];
                s.leg.useAnimatedBendNormal = false;
            }
        }
    }

    /// <summary>
    /// ปิด component LegIK ตอน weight = 0 — GrounderIK ตอน fade ถึง 0 ลดแค่ IKPositionWeight แต่ทิ้ง IKRotationWeight = 1
    /// กับ IKRotation (world) ค้างจากเฟรมสุดท้ายบนพื้น → LegIK ยังหมุนเท้าเข้ามุมเดิมทุกเฟรม
    /// = แดชแล้วเลี้ยวกลางอากาศ ตัวหันแต่เท้าค้างทิศเก่า (บิด) · ปิดทิ้งเลยเท้าเป็นอนิเมชั่น 100%
    /// เปิดกลับหลัง grounder initiate แล้วเท่านั้น (ก่อนนั้น IKRotation ยังเป็นท่าตอนเริ่มเกม)
    /// </summary>
    private void SetLegsEnabled(bool on)
    {
        if (grounder == null || grounder.legs == null) return;
        foreach (var leg in grounder.legs)
            if (leg != null && leg.enabled != on) leg.enabled = on;
    }

    private void Update()
    {
        bool spawned = controller.Object != null && controller.Object.IsValid;
        bool hidden = death != null && death.IsHiddenState;
        bool sliding = spawned && controller.IsSliding;
        bool active = spawned && controller.Grounded && !controller.IsGhostMode && !hidden
                      && (!sliding || activeWhileSliding);

        // prediction ของ Grounder มองล่วงหน้าตามความเร็ว — ที่ 16–20 m/s ของสไลด์จะวางเท้าไปไกลข้างหน้าเกินท่า
        grounder.solver.prediction = sliding ? 0f : _basePrediction;
        // สไลด์ตัวเอียงตามเนิน: ค่า default วัดความสูงตามแกนตั้งโลก → ท่านั่งไถลที่เท้ายื่นไปข้างหน้าบนเนินลง
        // ถูกมองว่าพื้นใต้เท้าต่ำกว่าเป็นขั้น แล้วกดสะโพกลงจนเท้าจมผิว ~16 ซม. · หมุนตาม root = วัดตามระนาบผิว
        grounder.solver.rotateSolver = sliding;

        Vector3 pos = transform.position;
        Vector3 delta = pos - _lastPos;
        bool warped = delta.sqrMagnitude > teleportDistance * teleportDistance;
        _lastPos = pos;

        // วิ่ง = เป้าเท้าไล่ความสูงพื้นช้าลง (ยืน/เดินขึ้นขั้นยังไวเท่าเดิม)
        if (!warped && Time.deltaTime > 0f)
        {
            delta.y = 0f;
            float raw = delta.magnitude / Time.deltaTime;
            _speed = Mathf.Lerp(_speed, raw, 1f - Mathf.Exp(-10f * Time.deltaTime));
        }
        // สไลด์ = ค่าเดิม (จูนเท้าไม่จมผิวทางไว้แล้วที่ความเร็วนั้น)
        float run01 = sliding ? 0f : Mathf.InverseLerp(runSpeedRange.x, runSpeedRange.y, _speed);
        grounder.solver.footSpeed = Mathf.Lerp(_baseFootSpeed, Mathf.Min(runFootSpeed, _baseFootSpeed), run01);
        if ((warped || (_wasHidden && !hidden)) && grounder.initiated) grounder.ResetPosition();
        _wasHidden = hidden;

        float target = active ? (sliding ? slideWeight : 1f) : 0f;
        float speed = target > grounder.weight ? fadeInSpeed : fadeOutSpeed;
        grounder.weight = Mathf.MoveTowards(grounder.weight, target, speed * Time.deltaTime);
        SetLegsEnabled(grounder.weight > 0f && grounder.initiated);
    }
}
