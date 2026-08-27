using Unity.Netcode;
using UnityEngine;

/// <summary>
/// จัดการสถานะ Freeze แบบ Global (ครั้งละ 1 ตัว) และ Broadcast สถานะให้คอมโพเนนต์บน GameObject เดียวกัน
/// ที่ implement IFreezeListener (เช่น RockSmasher) เพื่อให้หยุด/เดินต่อได้จริงทั้งเซิร์ฟและไคลเอนต์
/// เพิ่ม: โหมด Timed พร้อมอัปเดตเอฟเฟกต์กระพริบให้เร็วขึ้นเรื่อย ๆ
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class FreezableNet : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private Freezable freezable;       // ถ้ามี: ใช้หยุด DOTween/อนิเมชันภายในวัตถุ
    [SerializeField] private TargetHighlight highlight; // เอฟเฟกต์ไฮไลต์ (ถ้ามี)

    // จำกัดให้มีชิ้นเดียวที่ Freeze อยู่พร้อมกันทั้งเกม (ถือ state ฝั่งเซิร์ฟ)
    private static FreezableNet s_CurrentFrozen;

    // Timed mode runtime (ถือโดยเซิร์ฟเวอร์)
    private bool timedActive;
    private float timedEndTime;
    private float timedDuration;

    /// <summary>
    /// ใช้เช็คจากฝั่ง Client ว่าวัตถุนี้กำลังถูก freeze อยู่หรือไม่ (ป้องกันสแปม)
    /// </summary>
    public bool IsCurrentlyFrozen => s_CurrentFrozen == this;

    /// <summary>
    /// เช็คว่ามีวัตถุใดกำลังถูก freeze อยู่หรือไม่ (ใช้โดย TimeFreezeAbility_Net เพื่อปิดนาฬิกา)
    /// </summary>
    public static bool HasAnyFrozen => s_CurrentFrozen != null;

    private void Awake()
    {
        if (!freezable) freezable = GetComponent<Freezable>();
        if (!highlight) highlight = GetComponentInChildren<TargetHighlight>(true);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && s_CurrentFrozen == this)
        {
            // ปลด freeze state ให้ครบ (คืน Rigidbody, Animator ฯลฯ)
            // สำคัญมากสำหรับ pooling — ไม่งั้น object ที่กลับมาใช้ใหม่จะยังค้าง freeze
            timedActive = false;
            SetFrozen(false);
            s_CurrentFrozen = null;
        }
    }

    void Update()
    {
        // เฉพาะเซิร์ฟเวอร์คุมเวลา แล้วแจ้งวิชวลให้ทุกฝั่ง
        if (!IsServer) return;

        if (timedActive && s_CurrentFrozen == this)
        {
            float remaining = Mathf.Max(0f, timedEndTime - Time.time);
            float norm = (timedDuration <= 0.0001f) ? 0f : Mathf.Clamp01(remaining / timedDuration);

            // อัปเดตสัญญาณกระพริบ (เร็วขึ้นเมื่อ norm ↓)
            UpdateTimedPulseClientRpc(norm);
            UpdateTimedPulseLocal(norm);

            if (remaining <= 0f)
            {
                // หมดเวลา → ปลด freeze
                timedActive = false;
                SetFrozen(false);
                s_CurrentFrozen = null;
            }
        }
    }

    // ================== External API (เรียกจากผู้เล่นที่มีสกิล) ==================

    /// <summary>
    /// Toggle Freeze/Unfreeze ออบเจ็กต์นี้ (global: มีได้ครั้งละชิ้นเดียว)
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestToggleFreezeServerRpc()
    {
        // เมื่อเข้า Toggle → ตัดโหมด timed ทิ้ง
        timedActive = false;

        if (s_CurrentFrozen == this)
        {
            SetFrozen(false);
            s_CurrentFrozen = null;
        }
        else
        {
            if (s_CurrentFrozen) s_CurrentFrozen.SetFrozen(false);
            s_CurrentFrozen = this;
            SetFrozen(true);
            // Toggle mode = ไฮไลต์ติดค้าง (ไม่พัลส์)
            BroadcastTimedPulse(1f, pulseOn:false);
        }
    }

    /// <summary>
    /// Freeze แบบมีเวลา duration วินาที (global: เคลียร์ของเดิมก่อน)
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestTimedFreezeServerRpc(float duration)
    {
        // ป้องกันสแปมฝั่งเซิร์ฟ: ถ้าวัตถุนี้กำลังถูก freeze อยู่ ไม่ต่อเวลา
        if (timedActive && s_CurrentFrozen == this) return;

        duration = Mathf.Max(0.05f, duration);

        if (s_CurrentFrozen && s_CurrentFrozen != this)
            s_CurrentFrozen.SetFrozen(false);

        s_CurrentFrozen = this;
        timedActive = true;
        timedDuration = duration;
        timedEndTime = Time.time + duration;

        SetFrozen(true);

        // เริ่มต้นพัลส์ช้า (norm=1 → ช้า) แล้วจะเร่งขึ้นเมื่อ norm → 0
        BroadcastTimedPulse(1f, pulseOn:true);
    }

    /// <summary>
    /// รีเซ็ต global freeze: ถ้ามีชิ้นไหนกำลัง Freeze อยู่ ให้ปลดทันที
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestResetServerRpc()
    {
        if (s_CurrentFrozen != null)
        {
            s_CurrentFrozen.timedActive = false;
            s_CurrentFrozen.SetFrozen(false);
            s_CurrentFrozen = null;
        }
    }

    // ================== Server does real work + fan-out ==================

    private void SetFrozen(bool on)
    {
        // ทำ Local (ฝั่งเซิร์ฟ)
        if (freezable)
        {
            if (on) freezable.FreezeOn();
            else    freezable.FreezeOff();
        }
        ToggleFXLocal(on, forcePulse:false);
        NotifyFreezeListenersLocal(on);

        // แจ้งทุกไคลเอนต์ให้ทำซ้ำแบบ Local
        SetFrozenClientRpc(on);
    }

    [ClientRpc]
    private void SetFrozenClientRpc(bool on)
    {
        if (IsServer) return; // เซิร์ฟทำไปแล้ว

        if (freezable)
        {
            if (on) freezable.FreezeOn();
            else    freezable.FreezeOff();
        }
        ToggleFXLocal(on, forcePulse:false);
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
        if (!highlight) return;

        if (on)
        {
            if (forcePulse)
                highlight.SetForcedTimedVisual(1f); // เริ่ม norm=1 ช้าที่สุด
            else
                highlight.SetForced(true);          // โหมด Toggle = ติดค้าง
        }
        else
        {
            highlight.SetForced(false);
            highlight.ForceClearAll();
        }
    }

    // ====== Timed visual pulse fan-out ======

    void UpdateTimedPulseLocal(float normRemaining)
    {
        if (!highlight) return;
        // ถ้า timedActive → ใช้โหมดกระพริบ
        if (timedActive && s_CurrentFrozen == this)
            highlight.SetForcedTimedVisual(normRemaining);
    }

    void BroadcastTimedPulse(float normRemaining, bool pulseOn)
    {
        // เซิร์ฟทำ local ด้วย
        if (pulseOn) ToggleFXLocal(true, forcePulse:true);
        UpdateTimedPulseLocal(normRemaining);
        // แจ้งคลients
        UpdateTimedPulseClientRpc(normRemaining);
    }

    [ClientRpc]
    void UpdateTimedPulseClientRpc(float normRemaining)
    {
        if (IsServer) return; // เซิร์ฟจัดการเองแล้ว
        if (!highlight) return;
        highlight.SetForcedTimedVisual(normRemaining);
    }
}