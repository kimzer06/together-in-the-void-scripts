using UnityEngine;

/// <summary>
/// สกิล Time Freeze (RoleA) — เล็งวัตถุที่มี FreezableNet แล้วยิงเพื่อแช่ (Toggle / Timed)
/// ส่วนเล็ง/ปุ่ม/ไอเทม/เสียงยิงอยู่ใน <see cref="AimedAbility_Net"/>
/// คำสั่งแช่ส่งไปหา master (state authority ของ FreezableNet) — ทุกเครื่องเห็นผลจาก [Networked] ของวัตถุ
/// </summary>
public class TimeFreezeAbility_Net : AimedAbility_Net
{
    public enum FreezeMode { Toggle, Timed }

    [Header("Time Freeze — Target")]
    [SerializeField] LayerMask targetMask = ~0;
    [Tooltip("ถ้าตั้งไว้ จะใช้ layer นี้เป็น targetMask ตอนเริ่มเกม")]
    [SerializeField] string freezableLayerName = "Freezable";
    [Tooltip("layer เพิ่มที่ลำแช่ชนได้ (รวมกับ targetMask) — VoidCrystal: เกราะบังลำ / ประตูงอกที่แช่ได้")]
    [SerializeField] string[] extraBlockerLayers = { "VoidCrystal" };

    [Header("Freeze Mode (เลือกโหมดใน Inspector)")]
    [SerializeField] FreezeMode freezeMode = FreezeMode.Toggle;
    [Tooltip("ใช้เมื่อ FreezeMode = Timed")]
    [SerializeField] float timedDuration = 5f;

    [Header("Clock Animator")]
    [Tooltip("Animator ของไอเทมนาฬิกา – ใช้ Bool parameter 'isActive' เพื่อเล่นอนิเมชั่นหมุน")]
    [SerializeField] Animator clockAnimator;

    private static readonly int IsActiveParam = Animator.StringToHash("isActive");
    private const string ReActiveTrigger = "ReActive";

    /// <summary>
    /// เปิดนาฬิกาแล้วถ้ายังไม่เห็นของถูกแช่ภายในเวลานี้ให้ปิดเอง —
    /// คำสั่งแช่ต้องไป-กลับ master ก่อน ถ้าเช็ค HasAnyFrozen ทันทีนาฬิกาจะดับเฟรมถัดไป (เดิมเป็นแบบนั้นบนเครื่อง client)
    /// </summary>
    private const float ClockConfirmTimeout = 1f;

    bool _clockIsOn;
    bool _clockConfirmed;
    float _clockOnTime;
    TargetHighlight lastHighlight; // ใช้เมื่อเป้าหมายไม่มี FreezableNet (ไฮไลต์เดี่ยว)
    FreezableNet lastTarget;

    protected override PlayerRole DefaultRole => PlayerRole.RoleA;
    protected override Animator ItemAnimator => clockAnimator;

    protected override void AssignOwnerRefs()
    {
        if (!string.IsNullOrEmpty(freezableLayerName))
        {
            int mask = LayerMask.GetMask(freezableLayerName);
            if (mask != 0) targetMask = mask;
        }
        if (extraBlockerLayers != null && extraBlockerLayers.Length > 0)
            targetMask |= LayerMask.GetMask(extraBlockerLayers);
    }

    protected override void ResolveAnimators()
    {
        // เลือก Animator ของ "ตัวละครผู้เล่น" โดยกัน clock/item animator ออก
        if (skillAnimator) return;
        foreach (var a in GetComponentsInChildren<Animator>(true))
        {
            if (!a || a == clockAnimator) continue;
            skillAnimator = a;
            break;
        }
    }

    protected override void OwnerUpdate()
    {
        if (isAiming) UpdateAimTarget();

        // ของที่ถูก freeze กลับมาเคลื่อนไหว (timed หมดเวลา / toggle off) → ปิดนาฬิกาอัตโนมัติ
        if (_clockIsOn)
        {
            if (FreezableNet.HasAnyFrozen) _clockConfirmed = true;
            else if (_clockConfirmed || Time.time - _clockOnTime > ClockConfirmTimeout) SetClockActive(false);
        }
    }

    protected override void OnAimStopped() => ClearTarget();

    void UpdateAimTarget()
    {
        if (!playerCam) return;

        // จอย: crosshair เกาะของที่แช่ได้อยู่ = เป้าคือตัวนั้นเลย (เส้นผ่าน crosshair ก็ชี้ไปที่มันอยู่แล้ว)
        var snapped = SnapTarget ? SnapTarget.GetComponent<FreezableNet>() : null;
        if (snapped && !(snapped.CanFreezeNow || snapped.IsCurrentlyFrozen)) snapped = null;

        RaycastHit hit = default;
        if (snapped || RaycastAim(AimRay(), aimMaxDistance, targetMask, out hit))
        {
            var target = snapped ? snapped : hit.collider.GetComponentInParent<FreezableNet>();
            // โดนคริสตัลที่ไม่ได้เป็นตัวแช่เอง (เกราะ Crystal_Shell ที่เป็นลูกของของแช่ได้ เช่นหินที่ขยับ) = ถูกบัง
            // ไม่งั้น GetComponentInParent ไต่ขึ้นไปเจอ FreezableNet ของพ่อแล้วแช่ทะลุเกราะ
            bool shielded = false;
            if (!snapped && target)
            {
                var crystal = hit.collider.GetComponentInParent<VoidCrystal>();
                shielded = crystal && !target.transform.IsChildOf(crystal.transform);
                if (shielded) target = null;
            }
            // ยังแช่ไม่ได้ (เช่นประตูคริสตัลที่ Bobby ยังไม่ยิงแตก) = ถูกบัง ไม่ไฮไลต์ — ถ้าแช่อยู่แล้วยังเล็งปลดได้
            if (target && !target.CanFreezeNow && !target.IsCurrentlyFrozen) target = null;
            // ถ้ามี FreezableNet ให้มันเป็นคนขับไฮไลต์ (จะทาทั้งกลุ่มที่ลิงก์ไว้ เช่น ประตูคู่)
            var h = target || snapped || shielded ? null : hit.collider.GetComponentInParent<TargetHighlight>();

            if (target != lastTarget || h != lastHighlight)
            {
                ClearAimVisual();
                lastTarget = target;
                lastHighlight = h;
            }

            // ย้ำทุกเฟรม: ตอนปลด freeze จะมี ForceClearAll() ล้าง flag ของ aim ทิ้งไปด้วย
            if (lastTarget) lastTarget.SetAimHighlight(true);
            else if (lastHighlight) lastHighlight.SetAimActive(true);
        }
        else
        {
            ClearTarget();
        }
        SetCrosshairLocked(lastTarget != null);
    }

    /// <summary>crosshair เกาะได้: ของที่แช่ได้ตอนนี้ (หรือแช่อยู่ — เล็งปลดได้)</summary>
    protected override bool AcceptsSnapTarget(AimAssistTarget t)
    {
        if (!t.Is(AimAssistTarget.TargetKind.Freezable)) return false;
        var f = t.GetComponent<FreezableNet>();
        return f && (f.CanFreezeNow || f.IsCurrentlyFrozen);
    }

    // ---------- Trigger (Toggle / Timed) & Reset ----------

    protected override void OnFire()
    {
        if (!isAiming || lastTarget == null) return;

        PlayShootSfx(transform.position);

        if (freezeMode == FreezeMode.Toggle)
        {
            // ถ้า target ถูก freeze อยู่ → toggle off → ปิดนาฬิกา
            bool willUnfreeze = lastTarget.IsCurrentlyFrozen;
            lastTarget.RequestToggleFreeze();
            PlaySkillActivateTrigger();
            SetClockActive(!willUnfreeze);
        }
        else // FreezeMode.Timed
        {
            // ป้องกันสแปม: ถ้า target กำลังถูก freeze อยู่แล้ว ไม่ให้ยิงซ้ำ
            if (lastTarget.IsCurrentlyFrozen) return;

            lastTarget.RequestTimedFreeze(Mathf.Max(0.05f, timedDuration));
            PlaySkillActivateTrigger();

            // ถ้านาฬิกาหมุนอยู่แล้ว (ของเก่าหลุด ของใหม่โดน) → ยิง ReActive เพื่อรีสตาร์ทอนิเมชั่น
            if (_clockIsOn)
            {
                SetAnimTrigger(AnimSlot.Item, ReActiveTrigger);
                _clockConfirmed = false;
                _clockOnTime = Time.time;
            }
            else
            {
                SetClockActive(true);
            }
        }
    }

    protected override void OnReset()
    {
        if (lastTarget != null) lastTarget.RequestReset();
        else FreezableNet.RequestResetAny();

        // หยุดอนิเมชั่นนาฬิกา
        SetClockActive(false);
        SnapSkillLayerWeight(0f);
    }

    void ClearTarget()
    {
        ClearAimVisual();
        lastHighlight = null;
        lastTarget = null;
    }

    /// <summary>ปิดไฮไลต์ของเป้าหมายปัจจุบัน (ทั้งกลุ่มถ้าเป็น FreezableNet)</summary>
    void ClearAimVisual()
    {
        if (lastTarget) lastTarget.SetAimHighlight(false);
        if (lastHighlight) lastHighlight.SetAimActive(false);
    }

    // ---------- Clock Animator ----------

    /// <summary>Bool ของนาฬิกาซิงค์ให้เครื่องอื่นผ่าน NetworkMecanimAnimator (ParameterBools) ของไอเทม</summary>
    void SetClockActive(bool active)
    {
        _clockIsOn = active;
        _clockConfirmed = false;
        _clockOnTime = Time.time;
        if (clockAnimator) clockAnimator.SetBool(IsActiveParam, active);
    }
}
