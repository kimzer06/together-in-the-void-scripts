using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ข้ามคัตซีนแบบ co-op — ทุกคนในห้องต้องกด E ค้างจนครึ่งวงของตัวเองเต็ม "พร้อมกัน" ถึงจะข้าม
/// (เหลือคนเดียวในห้อง = ครึ่งของตัวเองเต็มก็ข้าม) — logic กดค้าง/เติม/ตัดสินอยู่ใน CoopHoldVote
///
/// ส่วนของตัวนี้: นับเฉพาะตอนคัตซีนเล่นอยู่ · ปุ่มโผล่เมื่อกดปุ่มใดๆ (ShowTimer) · Ctrl+E ข้ามคนเดียว (Editor)
/// จบ → ทุกเครื่องเรียก TimelineController.SkipToEnd() พร้อมกัน
///
/// prefab: Prefabs/UI/CutsceneSkip — วางเป็นลูกของ root คัตซีน (ตัวที่มี TimelineController)
/// </summary>
public class CutsceneSkipVote : CoopHoldVote
{
    private const float PokeInterval = 0.5f;

    [Header("References")]
    [Tooltip("ถ้าเว้นว่างจะหาจาก parent")]
    [SerializeField] private TimelineController timeline;
    [SerializeField] private CutsceneSkipView view;

    [Header("Show")]
    [Tooltip("กดปุ่มใดๆ แล้วปุ่มข้ามโผล่ค้างไว้กี่วินาที (นับใหม่ทุกครั้งที่กด)")]
    [SerializeField, Min(0.1f)] private float showSeconds = 2.5f;

    // master เขียน
    [Networked] private TickTimer ShowTimer { get; set; }

    private float _lastPokeTime = -99f;
    private float _localShowUntil = -99f;
#pragma warning disable CS0649 // ใน build ไม่มีใครเขียน (เป็น 0 ตลอด)
    private float _devFill;   // Ctrl+E (Editor เท่านั้น)
#pragma warning restore CS0649
#if UNITY_EDITOR
    private bool _devSent;
#endif

    // นับเฉพาะตอนคัตซีนเครื่องนี้เล่นอยู่ — hold ที่ส่งมาก่อนยังเก็บไว้ ไม่งั้นต้องปล่อยแล้วกดใหม่
    protected override bool IsVoteOpen => timeline != null && timeline.IsPlaying;

    protected override bool CanHoldLocally() => IsVoteOpen;

    protected override void OnVoteSpawned()
    {
        if (timeline == null) timeline = GetComponentInParent<TimelineController>();
        if (view == null) view = GetComponentInChildren<CutsceneSkipView>(true);
    }

    // SkipToEnd รอจนคัตซีนเครื่องนี้เริ่มเล่นเองถ้ายังไม่เริ่ม (กรณีเข้าห้องมาทีหลัง)
    protected override void OnCompleted()
    {
        if (timeline != null) timeline.SkipToEnd();
    }

    // ───────────────────── master ─────────────────────

    protected override void OnMasterTick()
    {
        if (AnyHolding) ShowTimer = TickTimer.CreateFromSeconds(Runner, showSeconds);
    }

    protected override void OnHoldReported() =>
        ShowTimer = TickTimer.CreateFromSeconds(Runner, showSeconds);

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_Poke()
    {
        if (!IsCompleted) ShowTimer = TickTimer.CreateFromSeconds(Runner, showSeconds);
    }

    // ทางลัด dev (Ctrl+E ค้าง) — ฝั่งส่งมีเฉพาะใน Editor (UpdateDevSoloSkip)
    // ตัว RPC ต้องคอมไพล์ทุก build: ถ้าครอบ #if ลำดับ RPC ของ Editor กับ build จะไม่ตรงกัน เล่นข้ามกันแล้วพัง
    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_DevSoloSkip()
    {
        if (IsCompleted || !IsVoteOpen) return;
        CompleteNow();
    }

    // ───────────────────── ทุกเครื่อง ─────────────────────

    protected override void RenderVote(bool canHold, bool held, float a, float b)
    {
        float dt = Time.unscaledDeltaTime;
        ReadPoke(canHold);
        UpdateDevSoloSkip(held, dt);

        if (view == null) return;

        if (_devFill > 0f)
        {
            // Ctrl+E = ข้ามคนเดียว → เติมให้ทั้งสองครึ่ง ให้เห็นว่าไม่ต้องรอเพื่อน
            a = Mathf.Max(a, _devFill);
            b = Mathf.Max(b, _devFill);
        }
        view.SetFill(a, b);

        bool playing = IsVoteOpen && !IsCompleted;
        bool visible = playing && (
            !ShowTimer.ExpiredOrNotRunning(Runner) ||
            Time.unscaledTime < _localShowUntil ||
            a > 0f || b > 0f);
        view.SetVisible(visible);
    }

    /// <summary>กดปุ่มใดๆ / คลิก → ปุ่มข้ามโผล่ (เครื่องตัวเองทันที + บอก master ให้เพื่อนเห็นด้วย)</summary>
    private void ReadPoke(bool canHold)
    {
        if (!canHold) return; // canHold = ไม่ pause + คัตซีนเล่นอยู่

        if (GameInput.AnyButtonPressed)
        {
            float now = Time.unscaledTime;
            _localShowUntil = now + showSeconds;
            if (now - _lastPokeTime >= PokeInterval)
            {
                _lastPokeTime = now;
                RPC_Poke();
            }
        }
    }

    /// <summary>
    /// Editor เท่านั้น: Ctrl+E ค้างจนเต็ม = ข้ามคนเดียวโดยไม่ต้องรอเพื่อน (เทส MPPM ไม่ต้องสลับหน้าต่างไปกดอีกเครื่อง)
    /// ใน build ฟังก์ชันนี้ว่าง → _devFill เป็น 0 ตลอด
    /// </summary>
    private void UpdateDevSoloSkip(bool held, float dt)
    {
#if UNITY_EDITOR
        bool devHeld = held && Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
        if (!devHeld)
        {
            _devFill = 0f;
            _devSent = false;
            return;
        }

        _devFill = Mathf.MoveTowards(_devFill, 1f, dt / HoldDuration);
        if (_devFill >= 1f && !_devSent)
        {
            _devSent = true;
            RPC_DevSoloSkip();
        }
#endif
    }
}
