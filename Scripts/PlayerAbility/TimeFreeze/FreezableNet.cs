using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// จัดการสถานะ Freeze แบบ Global (ครั้งละ 1 ตัว) และ Broadcast สถานะให้คอมโพเนนต์บน GameObject เดียวกัน
/// ที่ implement IFreezeListener (เช่น RockSmasher) เพื่อให้หยุด/เดินต่อได้จริงทุกเครื่อง
/// โหมด Timed: เอฟเฟกต์กระพริบเร็วขึ้นเรื่อยๆ จนหมดเวลา
///
/// Fusion Shared Mode: ของที่แช่ได้ทุกชิ้นเป็นของ master (scene object / master สปอน)
/// - ผู้เล่นขอแช่/ปลด/รีเซ็ตผ่าน RPC → master ตัดสิน แล้วเขียน [Networked]
/// - ทุกเครื่อง apply ภาพ/ฟิสิกส์เองเมื่อค่าเปลี่ยน (เดิม ClientRpc) · เวลาที่เหลือคำนวณเองจาก TickTimer
///   (เดิม server ยิง ClientRpc บอกเปอร์เซ็นต์ทุกเฟรม)
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class FreezableNet : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private Freezable freezable;       // ถ้ามี: ใช้หยุด DOTween/อนิเมชันภายในวัตถุ
    [SerializeField] private TargetHighlight highlight; // เอฟเฟกต์ไฮไลต์ (ถ้ามี)

    [Header("Aim Snap")]
    [Tooltip("จุดที่ crosshair เกาะ (Transform ลูก) — ว่าง = ของเล็ก (< 2 ม.) เกาะกลางกล่อง collider / ของใหญ่เกาะจุดที่ใกล้กลางจอ\n" +
             "ตั้งแล้วเกาะจุดนี้จุดเดียวทุกขนาด · ต้องอยู่บน/ในตัวของ (เส้นจากกล้องต้องโดนของก่อน ไม่งั้นถือว่าถูกบัง) · คลิกขวาที่ component → Create Aim Snap Point")]
    [SerializeField] private Transform aimSnapPoint;

    /// <summary>
    /// highlight ของตัวเอง + ของวัตถุที่ลิงก์ไว้ (เช่น ประตูคู่ที่ตั้ง Linked Doors)
    /// สร้างครั้งเดียวตอนใช้งานจริง ไม่สร้างใน Awake เพราะผู้ให้ข้อมูลกลุ่ม
    /// (ILinkedHighlightProvider) อาจยังไม่ผ่าน Awake ของตัวเอง
    /// </summary>
    private TargetHighlight[] _highlightGroup;

    // ───────────────────────────────────────────────────────────────────────
    // จำกัดให้มีชิ้นเดียวที่ Freeze อยู่พร้อมกันทั้งเกม
    // s_AuthorityCurrentFrozen = bookkeeping ของ master เท่านั้น (ห้ามเครื่องอื่นอ่าน)
    // เครื่องอื่นดูจาก IsFrozen ที่ซิงค์จริง และ s_Frozen ที่อัปเดตตอน apply
    // ───────────────────────────────────────────────────────────────────────
    private static FreezableNet s_AuthorityCurrentFrozen;

    /// <summary>วัตถุที่กำลัง freeze อยู่บนเครื่องนี้ (อัปเดตตอน apply ทุกเครื่อง)</summary>
    private static readonly HashSet<FreezableNet> s_Frozen = new();

    /// <summary>ทุกชิ้นที่ spawn อยู่ — ใช้หาตัวส่งคำขอรีเซ็ตเมื่อผู้เล่นไม่ได้เล็งอะไร</summary>
    private static readonly List<FreezableNet> s_Spawned = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        s_AuthorityCurrentFrozen = null;
        s_Frozen.Clear();
        s_Spawned.Clear();
    }

    [Networked] private NetworkBool IsFrozen { get; set; }
    [Networked] private NetworkBool IsTimed { get; set; }
    [Networked] private TickTimer FreezeTimer { get; set; }
    [Networked] private float TimedDuration { get; set; }

    private ChangeDetector _changes;
    private bool _appliedFrozen;  // สถานะที่ apply ลงเครื่องนี้แล้ว (master apply ทันที แล้วจะเจอการเปลี่ยนซ้ำใน Render)

    private bool IsSpawnedSafe => Object != null && Object.IsValid;

    /// <summary>
    /// วัตถุนี้กำลังถูก freeze อยู่หรือไม่ (ใช้กันสแปมฝั่งผู้เล่น) — ค่าที่ซิงค์จริง อ่านได้ทุกเครื่อง
    /// </summary>
    public bool IsCurrentlyFrozen => IsSpawnedSafe && IsFrozen;

    /// <summary>ตอนนี้เริ่มแช่ได้ไหม — ถาม IFreezeGate บน GameObject เดียวกัน (ไม่มี = ได้เสมอ) · ใช้ทั้งฝั่งเล็งและ master</summary>
    public bool CanFreezeNow
    {
        get
        {
            var gates = GetComponents<IFreezeGate>();
            for (int i = 0; i < gates.Length; i++)
                if (gates[i] != null && !gates[i].AllowFreeze) return false;
            return true;
        }
    }

    /// <summary>
    /// มีวัตถุใดกำลังถูก freeze อยู่หรือไม่ (ใช้โดย TimeFreezeAbility_Net เพื่อปิดนาฬิกา)
    /// </summary>
    public static bool HasAnyFrozen => s_Frozen.Count > 0;

    private void Awake()
    {
        AimAssistTarget.AttachTo(this, AimAssistTarget.TargetKind.Freezable).SetSnapPoint(aimSnapPoint);
        if (!freezable) freezable = GetComponent<Freezable>();
        if (!highlight) highlight = GetComponentInChildren<TargetHighlight>(true);
    }

    /// <summary>รวม highlight ของตัวเอง + ของกลุ่มที่ลิงก์ไว้ (กรองค่าว่าง/ซ้ำแล้ว)</summary>
    private void EnsureHighlightGroup()
    {
        if (_highlightGroup != null) return;

        var list = new List<TargetHighlight>();
        if (highlight) list.Add(highlight);

        var providers = GetComponents<ILinkedHighlightProvider>();
        for (int i = 0; i < providers.Length; i++)
            providers[i]?.CollectLinkedHighlights(list);

        for (int i = list.Count - 1; i >= 0; i--)
            if (!list[i] || list.IndexOf(list[i]) != i) list.RemoveAt(i);

        _highlightGroup = list.ToArray();
    }

    /// <summary>
    /// ไฮไลต์ตอนถูกเล็ง — ทาทั้งกลุ่ม (เรียกจาก TimeFreezeAbility_Net ฝั่งเจ้าของเท่านั้น
    /// เป็นวิชวลเฉพาะคนเล็ง ไม่ต้องซิงค์)
    /// </summary>
    public void SetAimHighlight(bool on)
    {
        EnsureHighlightGroup();
        for (int i = 0; i < _highlightGroup.Length; i++)
        {
            var h = _highlightGroup[i];
            if (h) h.SetAimActive(on);
        }
    }

    public override void Spawned()
    {
        s_Spawned.Add(this);
        _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);

        // เผื่อเข้ามาทีหลัง: ของที่ถูกแช่อยู่แล้วตอนเรา spawn (ตัวจับการเปลี่ยนไม่นับค่าเริ่มต้น)
        if (IsFrozen) ApplyFrozenLocal(true);
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        s_Spawned.Remove(this);
        s_Frozen.Remove(this);
        _appliedFrozen = false;
        if (s_AuthorityCurrentFrozen == this) s_AuthorityCurrentFrozen = null;
    }

    public override void Render()
    {
        foreach (var change in _changes.DetectChanges(this))
        {
            if (change == nameof(IsFrozen)) ApplyFrozenLocal(IsFrozen);
        }

        // Timed: กระพริบเร็วขึ้นเมื่อเวลาที่เหลือ ↓ — ทุกเครื่องคำนวณเองจาก TickTimer
        if (_appliedFrozen && IsTimed)
        {
            float remaining = FreezeTimer.RemainingTime(Runner) ?? 0f;
            float norm = TimedDuration <= 0.0001f ? 0f : Mathf.Clamp01(remaining / TimedDuration);
            ApplyTimedPulse(norm);
        }
    }

    public override void FixedUpdateNetwork()
    {
        // master คุมเวลา — หมดเวลา → ปลด freeze
        if (!HasStateAuthority || !IsFrozen || !IsTimed) return;
        if (!FreezeTimer.Expired(Runner)) return;

        Authority_SetFrozen(false, false, 0f);
        if (s_AuthorityCurrentFrozen == this) s_AuthorityCurrentFrozen = null;
    }

    // ================== External API (เรียกจากผู้เล่นที่มีสกิล — เครื่องไหนก็ได้) ==================

    /// <summary>Toggle Freeze/Unfreeze ออบเจ็กต์นี้ (global: มีได้ครั้งละชิ้นเดียว)</summary>
    public void RequestToggleFreeze()
    {
        if (IsSpawnedSafe) RPC_RequestToggleFreeze();
    }

    /// <summary>Freeze แบบมีเวลา duration วินาที (global: เคลียร์ของเดิมก่อน)</summary>
    public void RequestTimedFreeze(float duration)
    {
        if (IsSpawnedSafe) RPC_RequestTimedFreeze(duration);
    }

    /// <summary>รีเซ็ต global freeze: ถ้ามีชิ้นไหนกำลัง Freeze อยู่ ให้ปลดทันที</summary>
    public void RequestReset()
    {
        if (IsSpawnedSafe) RPC_RequestReset();
    }

    /// <summary>รีเซ็ต global freeze โดยไม่ต้องมีเป้า — ส่งคำขอผ่านชิ้นไหนก็ได้ที่ spawn อยู่</summary>
    public static void RequestResetAny()
    {
        for (int i = 0; i < s_Spawned.Count; i++)
        {
            var f = s_Spawned[i];
            if (f != null && f.IsSpawnedSafe)
            {
                f.RequestReset();
                return;
            }
        }
    }

    // ================== Master ==================

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_RequestToggleFreeze()
    {
        // เมื่อเข้า Toggle → ตัดโหมด timed ทิ้ง
        if (s_AuthorityCurrentFrozen == this)
        {
            Authority_SetFrozen(false, false, 0f);
            s_AuthorityCurrentFrozen = null;
        }
        else
        {
            if (!CanFreezeNow) return; // เช่นประตูคริสตัลที่ยังไม่ถูกยิงแตก
            ReleaseCurrentFrozen();
            s_AuthorityCurrentFrozen = this;
            // Toggle mode = ไฮไลต์ติดค้าง (ไม่พัลส์)
            Authority_SetFrozen(true, false, 0f);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_RequestTimedFreeze(float duration)
    {
        // ป้องกันสแปมฝั่ง master: ถ้าวัตถุนี้กำลังถูก freeze อยู่ ไม่ต่อเวลา
        if (IsTimed && IsFrozen && s_AuthorityCurrentFrozen == this) return;
        if (!CanFreezeNow) return;

        duration = Mathf.Max(0.05f, duration);

        if (s_AuthorityCurrentFrozen != this) ReleaseCurrentFrozen();

        s_AuthorityCurrentFrozen = this;
        // เริ่มพัลส์ช้า (norm=1) แล้วเร่งขึ้นเมื่อเวลาเหลือน้อย
        Authority_SetFrozen(true, true, duration);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_RequestReset()
    {
        ReleaseCurrentFrozen();
    }

    private static void ReleaseCurrentFrozen()
    {
        var current = s_AuthorityCurrentFrozen;
        s_AuthorityCurrentFrozen = null;
        if (current != null && current.IsSpawnedSafe && current.HasStateAuthority)
            current.Authority_SetFrozen(false, false, 0f);
    }

    private void Authority_SetFrozen(bool on, bool timed, float duration)
    {
        IsTimed = on && timed;
        TimedDuration = IsTimed ? duration : 0f;
        FreezeTimer = IsTimed ? TickTimer.CreateFromSeconds(Runner, duration) : TickTimer.None;
        IsFrozen = on;

        // master apply ทันที ไม่ต้องรอ Render รอบหน้า
        ApplyFrozenLocal(on);
    }

    // ================== ทุกเครื่อง ==================

    private void ApplyFrozenLocal(bool on)
    {
        if (on == _appliedFrozen) return;
        _appliedFrozen = on;

        if (on) s_Frozen.Add(this);
        else s_Frozen.Remove(this);

        if (freezable)
        {
            if (on) freezable.FreezeOn();
            else    freezable.FreezeOff();
        }
        // Timed = กระพริบ (เริ่ม norm=1 ช้าสุด), Toggle = ติดค้าง
        // (เดิม client ได้ภาพกระพริบแม้เป็นโหมด Toggle — host ติดค้าง ตอนนี้ตรงกันทุกเครื่อง)
        ToggleFXLocal(on, forcePulse: on && IsTimed);
        NotifyFreezeListenersLocal(on);
    }

    private void NotifyFreezeListenersLocal(bool on)
    {
        var monos = GetComponents<MonoBehaviour>();
        for (int i = 0; i < monos.Length; i++)
        {
            if (monos[i] is IFreezeListener listener)
                listener.OnFreezeChanged(on);
        }
    }

    private void ToggleFXLocal(bool on, bool forcePulse)
    {
        EnsureHighlightGroup();

        for (int i = 0; i < _highlightGroup.Length; i++)
        {
            var h = _highlightGroup[i];
            if (!h) continue;

            if (on)
            {
                if (forcePulse)
                    h.SetForcedTimedVisual(1f); // เริ่ม norm=1 ช้าที่สุด
                else
                    h.SetForced(true);          // โหมด Toggle = ติดค้าง
            }
            else
            {
                h.SetForced(false);
                h.ForceClearAll();
            }
        }
    }

    /// <summary>ทาภาพกระพริบ (Timed) ให้ทั้งกลุ่ม</summary>
    private void ApplyTimedPulse(float normRemaining)
    {
        EnsureHighlightGroup();
        for (int i = 0; i < _highlightGroup.Length; i++)
        {
            var h = _highlightGroup[i];
            if (h) h.SetForcedTimedVisual(normRemaining);
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Create Aim Snap Point")]
    private void CreateAimSnapPoint()
    {
        if (aimSnapPoint)
        {
            UnityEditor.Selection.activeTransform = aimSnapPoint;
            return;
        }
        var point = AimAssistTarget.EditorCreateSnapPoint(this);
        UnityEditor.Undo.RecordObject(this, "Create Aim Snap Point");
        aimSnapPoint = point;
        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
    }

    /// <summary>จุดที่ crosshair เกาะ — Crystal_Gate_Regrow (มี VoidCrystal ด้วย) เกาะกลางเสมอ</summary>
    private void OnDrawGizmosSelected()
        => AimAssistTarget.EditorDrawSnapGizmo(this, aimSnapPoint, GetComponent<VoidCrystal>());
#endif
}
