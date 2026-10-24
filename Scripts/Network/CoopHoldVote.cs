using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ฐานของ "กด E ค้างพร้อมกันทุกคน" แบบ co-op (ข้ามคัตซีน, Core Star) — ทุกคนในห้องต้องกดจนครึ่งของตัวเองเต็มพร้อมกัน
/// (เหลือคนเดียวในห้อง = ครึ่งของตัวเองเต็มก็จบ)
///
/// เครื่องเจ้าของรายงานแค่ "กดค้างอยู่ไหม" → master เดิน Fill ใน FixedUpdateNetwork และตัดสิน Done
/// → ทุกเครื่องจับ Done ด้วย ChangeDetector แล้วเรียก OnCompleted() พร้อมกัน (เข้าห้องมาทีหลังก็ได้ใน Spawned)
/// ครึ่งของตัวเองวาดจากค่าที่จำลองในเครื่อง (ไม่รอ round-trip) ครึ่งของเพื่อนวาดจาก [Networked]
///
/// ลูกเติมแค่ hook: IsVoteOpen (master นับไหม) · CanHoldLocally (เครื่องนี้กดได้ไหม) · OnCompleted · RenderVote (วาด)
/// Spawned/FixedUpdateNetwork/Render ถูก sealed ไว้ — ใช้ OnVoteSpawned / OnMasterTick / OnNetworkedChanged แทน
/// </summary>
public abstract class CoopHoldVote : NetworkBehaviour
{
    private const float HoldResendInterval = 0.5f; // ส่ง "ยังค้างอยู่" ซ้ำ เผื่อ master ยังหาตัวละครเราไม่เจอตอนกดครั้งแรก

    [Header("Hold")]
    [Tooltip("กดค้างกี่วินาทีครึ่งของตัวเองถึงจะเต็ม")]
    [SerializeField, Min(0.05f)] private float holdDuration = 1f;
    [Tooltip("ปล่อยมือแล้วใช้กี่วินาทีไหลจากเต็มจนหมด")]
    [SerializeField, Min(0.05f)] private float drainDuration = 0.4f;

    // master เขียน
    [Networked] private NetworkBool HoldA { get; set; }
    [Networked] private NetworkBool HoldB { get; set; }
    [Networked] private float FillA { get; set; }
    [Networked] private float FillB { get; set; }
    [Networked] private NetworkBool Done { get; set; }

    private ChangeDetector _changes;

    // ฝั่ง master
    private readonly Dictionary<PlayerRef, PlayerRole> _roles = new();
    private PlayerRef _holderA = PlayerRef.None;
    private PlayerRef _holderB = PlayerRef.None;

    // ฝั่งเครื่องตัวเอง
    private bool _sentHold;
    private float _lastHoldSendTime;
    private float _localFill;
    private float _shownA, _shownB;
    private float _nextLocalRoleTry;

    protected float HoldDuration => holdDuration;
    protected bool IsCompleted => Done;
    protected bool AnyHolding => HoldA || HoldB;

    /// <summary>role นี้กดค้างอยู่ไหม (ค่าจาก master — ตัวเราเองใช้ค่า held ใน RenderVote จะไวกว่า)</summary>
    protected bool IsRoleHolding(PlayerRole role) => role == PlayerRole.RoleA ? HoldA : HoldB;

    // ───────────────────── hook ─────────────────────

    /// <summary>master: นับ Fill ไหม (false = รีเซ็ต Fill เป็น 0 แต่ hold ที่ส่งมาก่อนยังเก็บไว้)</summary>
    protected abstract bool IsVoteOpen { get; }

    /// <summary>เครื่องนี้: กด E ได้ไหม (ฐานเช็ค Done / PauseMenu / มีคีย์บอร์ดให้แล้ว)</summary>
    protected abstract bool CanHoldLocally();

    /// <summary>ทุกเครื่อง ครั้งเดียว — ตอน Done เปลี่ยนเป็น true หรือ Spawned แล้วเจอว่าจบไปแล้ว</summary>
    protected abstract void OnCompleted();

    /// <summary>ทุกเครื่อง ทุกเฟรม — fillA/fillB ใส่ค่าเครื่องตัวเองไว้ในครึ่งของเราแล้ว</summary>
    protected abstract void RenderVote(bool canHold, bool held, float fillA, float fillB);

    /// <summary>ก่อนสร้าง ChangeDetector — หา reference ที่นี่</summary>
    protected virtual void OnVoteSpawned() { }

    /// <summary>master ทุก tick ระหว่างเปิดนับ (หลังเดิน Fill)</summary>
    protected virtual void OnMasterTick() { }

    /// <summary>master: มีคนรายงานกด/ปล่อย</summary>
    protected virtual void OnHoldReported() { }

    /// <summary>ทุกเครื่อง: [Networked] ของลูกเปลี่ยน (ใช้ ChangeDetector ตัวเดียวกับฐาน)</summary>
    protected virtual void OnNetworkedChanged(string propertyName) { }

    /// <summary>master เท่านั้น: จบเลยไม่ต้องรอ Fill (เช่น dev skip)</summary>
    protected void CompleteNow()
    {
        if (HasStateAuthority && !Done) Done = true;
    }

    // ───────────────────── lifecycle ─────────────────────

    public sealed override void Spawned()
    {
        OnVoteSpawned();
        _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);

        // เข้ามาหลังจาก master ตัดสินไปแล้ว
        if (Done) OnCompleted();
    }

    // ───────────────────── master ─────────────────────

    public sealed override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority || Done) return;

        if (!IsVoteOpen)
        {
            FillA = 0f;
            FillB = 0f;
            return;
        }

        // คนหลุดกลางคัน → ปล่อยมือแทนเขา
        if (HoldA && !FusionPlayers.IsActive(Runner, _holderA)) HoldA = false;
        if (HoldB && !FusionPlayers.IsActive(Runner, _holderB)) HoldB = false;

        float dt = Runner.DeltaTime;
        FillA = Step(FillA, HoldA, dt);
        FillB = Step(FillB, HoldB, dt);

        OnMasterTick();

        if (!Done && (FillA >= 1f || FillB >= 1f) && AllPresentPlayersFull())
            Done = true;
    }

    private float Step(float fill, bool holding, float dt) =>
        holding ? Mathf.MoveTowards(fill, 1f, dt / holdDuration)
                : Mathf.MoveTowards(fill, 0f, dt / drainDuration);

    private bool AllPresentPlayersFull()
    {
        bool any = false;
        foreach (var p in Runner.ActivePlayers)
        {
            if (!TryGetRole(p, out var role)) return false; // ยังไม่รู้ว่าเป็นใคร → ยังไม่จบ
            if ((role == PlayerRole.RoleA ? FillA : FillB) < 1f) return false;
            any = true;
        }
        return any;
    }

    protected bool TryGetRole(PlayerRef player, out PlayerRole role)
    {
        if (_roles.TryGetValue(player, out role)) return true;

        if (FusionPlayers.TryGetPlayerDeath(Runner, player, out var death) &&
            death.TryGetComponent(out PlayerRoleFromLobby roleComp))
        {
            role = roleComp.Role;
            _roles[player] = role;
            return true;
        }
        return false;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_SetHolding(NetworkBool holding, RpcInfo info = default)
    {
        if (Done) return;
        var sender = FusionPlayers.SenderOf(Runner, info);
        if (!TryGetRole(sender, out var role)) return;

        if (role == PlayerRole.RoleA) { HoldA = holding; _holderA = sender; }
        else                          { HoldB = holding; _holderB = sender; }

        OnHoldReported();
    }

    // ───────────────────── ทุกเครื่อง ─────────────────────

    public sealed override void Render()
    {
        foreach (var change in _changes.DetectChanges(this))
        {
            if (change == nameof(Done)) { if (Done) OnCompleted(); }
            else OnNetworkedChanged(change);
        }

        bool canHold = !Done && !PauseMenu.IsOpen && CanHoldLocally();
        bool held = canHold && GameInput.InteractHeld; // E / Y (จอย)
        ReportHold(held);

        float dt = Time.unscaledDeltaTime;
        _localFill = held ? Mathf.MoveTowards(_localFill, 1f, dt / holdDuration)
                          : Mathf.MoveTowards(_localFill, 0f, dt / drainDuration);

        // ค่าจาก master มาเป็นช่วงๆ ตาม send rate → ไล่ตามให้ลื่น (เร็วกว่าความเร็วเติมจริง จะได้ไม่ตามหลัง)
        float follow = dt * 2f / Mathf.Min(holdDuration, drainDuration);
        _shownA = Mathf.MoveTowards(_shownA, FillA, follow);
        _shownB = Mathf.MoveTowards(_shownB, FillB, follow);

        float a = _shownA, b = _shownB;
        if (TryGetLocalRole(out var localRole))
        {
            if (localRole == PlayerRole.RoleA) a = _localFill;
            else b = _localFill;
        }

        RenderVote(canHold, held, a, b);
    }

    private void ReportHold(bool held)
    {
        float now = Time.unscaledTime;
        if (held != _sentHold || (held && now - _lastHoldSendTime >= HoldResendInterval))
        {
            _sentHold = held;
            _lastHoldSendTime = now;
            RPC_SetHolding(held);
        }
    }

    // TryGetRole หาไม่เจอจะไล่ FindObjectsByType — เรียกทุกเฟรมไม่ได้ เว้นช่วงไว้
    protected bool TryGetLocalRole(out PlayerRole role)
    {
        role = default;
        if (_roles.TryGetValue(Runner.LocalPlayer, out role)) return true;
        if (Time.unscaledTime < _nextLocalRoleTry) return false;
        _nextLocalRoleTry = Time.unscaledTime + 0.5f;
        return TryGetRole(Runner.LocalPlayer, out role);
    }
}
