using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// ตัวคุมกล่องแชท — วางหนึ่งตัวต่อซีน (prefab Prefabs/Dialogue/DialogueSystem มี DialogueUI เป็นลูก)
///
/// ใครก็ขอเล่นได้ผ่าน <see cref="Request"/> → master ถือคิว (priority → มาก่อน) แล้วสั่งทุกเครื่องเริ่ม sequence เดียวกันพร้อมกัน
/// แต่ละเครื่องเดินบรรทัดเองด้วยค่าจังหวะชุดเดียวกัน (อยู่ใน object นี้) จึงจบพร้อมกันโดยไม่ต้องซิงค์ทุกบรรทัด
/// master เริ่มตัวถัดไปเมื่อเครื่องตัวเองเล่นจบ · ระหว่างคัตซีน: คิวรอ + บรรทัดที่ค้างอยู่หยุดนับเวลา (UI ซ่อน)
/// ยกเว้นบทที่ติ๊ก <see cref="DialogueSequence.playDuringCutscene"/> — เริ่มได้ระหว่างคัตซีน ตัวที่ค้างอยู่ถูกพักไว้แล้วเล่นต่อหลังบทนี้จบ
///
/// บทพูดไม่ล็อกตัวละคร — เล่นต่อได้ตลอด
///
/// "คนเรียก" (triggerer) ส่งไปกับคำขอ → บรรทัดที่ตั้งผู้พูด = Triggerer/TriggererPartner กลายเป็น Lyra/Bobby ตามคนนั้น
/// ไม่ระบุคนเรียก = master ใช้ตัวละครของตัวเอง · master ตัดสินครั้งเดียวแล้วส่งค่าจริงให้ทุกเครื่อง (เห็นคนพูดตรงกัน)
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DialogueDirector : NetworkBehaviour
{
    public static DialogueDirector Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;

    [SerializeField] private DialogueUI view;

    [Header("จังหวะ — ทุกเครื่องคิดเวลาจากค่าชุดนี้")]
    [Tooltip("ความเร็วพิมพ์ (ตัวอักษร/วินาที)")]
    [SerializeField, Min(1f)] private float charsPerSecond = 40f;
    [Tooltip("ค้างหลังพิมพ์จบ (วินาที) + ตามความยาวข้อความ")]
    [SerializeField, Min(0f)] private float holdBase = 1.2f;
    [SerializeField, Min(0f)] private float holdPerChar = 0.035f;
    [SerializeField, Min(0.1f)] private float minLineSeconds = 1.5f;
    [Tooltip("เว้นระหว่าง sequence (วินาที)")]
    [SerializeField, Min(0f)] private float gapBetweenSequences = 0.6f;
    [Tooltip("คุยเล่น (Banter) รอคิวนานกว่านี้ = ทิ้ง ไม่ให้โผล่ผิดจังหวะ")]
    [SerializeField, Min(0f)] private float banterMaxWait = 10f;

    private struct Pending
    {
        public int Id;
        public DialoguePriority Priority;
        public float Since;
        public int Order;
        public byte Triggerer;
        public bool DuringCutscene;
    }

    // role ส่งข้ามเครื่องเป็น byte: 0 = ไม่ระบุ, 1 = RoleA, 2 = RoleB
    private const byte NoRole = 0;
    private static byte Encode(PlayerRole? r) => r == null ? NoRole : r == PlayerRole.RoleA ? (byte)1 : (byte)2;
    private static PlayerRole Decode(byte b) => b == 2 ? PlayerRole.RoleB : PlayerRole.RoleA;

    // master
    private readonly List<Pending> _queue = new();
    private readonly HashSet<int> _playedOnce = new();
    private int _orderCounter;
    private float _nextStartTime;

    // ขอมาก่อน spawn (เช่น UnityEvent ตอนซีนเริ่ม) — ส่งต่อหลัง Spawned
    private readonly List<(DialogueSequence seq, bool once, byte triggerer)> _preSpawn = new();

    // ทุกเครื่อง: sequence ที่กำลังเล่นอยู่ในเครื่องนี้
    private DialogueSequence _current;
    private PlayerRole _currentTriggerer;
    private int _lineIndex;
    private float _lineRemaining;

    // บทที่ถูกพักไว้ตอนบทประกอบคัตซีนแทรก — ทุกเครื่องพักเหมือนกันเพราะได้ RPC_Play ตัวเดียวกัน · เล่นต่อจากบรรทัดเดิมเมื่อบทแทรกจบ
    private DialogueSequence _suspended;
    private PlayerRole _suspendedTriggerer;
    private int _suspendedLine;

    public bool IsPlaying => _current != null;

    /// <summary>บทที่กำลังเล่นเป็นบทประกอบคัตซีน — DialogueUI ใช้ตัดสินว่าโชว์กล่องระหว่างคัตซีนไหม</summary>
    public bool CurrentPlaysDuringCutscene => _current != null && _current.playDuringCutscene;

    private bool IsPausedByCutscene =>
        _current != null && !_current.playDuringCutscene && TimelineController.IsAnyCutscenePlaying;
    private bool IsSpawned => Object != null && Object.IsValid;

    private void Awake()
    {
        if (view == null) view = GetComponentInChildren<DialogueUI>(true);
    }

    public override void Spawned()
    {
        Instance = this;
        foreach (var (seq, once, triggerer) in _preSpawn) SendRequest(seq, once, triggerer);
        _preSpawn.Clear();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (Instance == this) Instance = null;
        _queue.Clear();
        StopLocal();
    }

    // ───────────────────── API ─────────────────────

    /// <summary>
    /// ขอเล่นบทพูด — เรียกจากเครื่องไหนก็ได้ · once = เล่นครั้งเดียวทั้งด่าน (ขอซ้ำไม่มีผล)
    /// triggerer = คนที่ทำให้บทพูดเกิด (ใช้กับบรรทัดที่ผู้พูดเป็น Triggerer/TriggererPartner) — ขอซ้ำ คนแรกชนะ
    /// </summary>
    public static void Request(DialogueSequence seq, bool once = true, PlayerRole? triggerer = null)
    {
        if (seq == null || seq.lines.Count == 0) return;

        var d = Instance != null ? Instance : FindFirstObjectByType<DialogueDirector>();
        if (d == null)
        {
            Debug.LogWarning($"[Dialogue] ซีนนี้ไม่มี DialogueDirector — '{seq.name}' ไม่ถูกเล่น");
            return;
        }

        if (d.IsSpawned) d.SendRequest(seq, once, Encode(triggerer));
        else d._preSpawn.Add((seq, once, Encode(triggerer)));
    }

    /// <summary>ทิ้งออกจากคิว (ถ้ายังไม่ได้เล่น) — master เท่านั้น เช่น คำใบ้ของ puzzle ที่แก้ไปแล้ว · ตัวที่กำลังเล่นอยู่ปล่อยให้จบ</summary>
    public static void Cancel(DialogueSequence seq)
    {
        var d = Instance;
        if (seq == null || d == null || !d.IsSpawned || !d.HasStateAuthority) return;
        int id = seq.Id;
        d._queue.RemoveAll(p => p.Id == id);
    }

    private void SendRequest(DialogueSequence seq, bool once, byte triggerer)
    {
        if (HasStateAuthority) Enqueue(seq.Id, once, triggerer);
        else RPC_Request(seq.Id, once, triggerer);
    }

    /// <summary>role ของผู้เล่นคนนั้น (จาก prefab ตัวละคร) — ใช้ฝั่ง master ตัดสินคนเรียก</summary>
    public static bool TryGetPlayerRole(NetworkRunner runner, PlayerRef player, out PlayerRole role)
    {
        role = default;
        if (runner == null || player == PlayerRef.None) return false;
        if (!FusionPlayers.TryGetPlayerDeath(runner, player, out var death)) return false;
        if (!death.TryGetComponent(out PlayerRoleFromLobby r)) return false;
        role = r.Role;
        return true;
    }

    // ───────────────────── master ─────────────────────

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_Request(int id, NetworkBool once, byte triggerer) => Enqueue(id, once, triggerer);

    private void Enqueue(int id, bool once, byte triggerer)
    {
        if (!DialogueSequence.TryGet(id, out var seq)) return;
        if (once && !_playedOnce.Add(id)) return;

        // ทั้งสองเครื่องยิง UnityEvent เดียวกัน → ขอซ้ำ ตัดทิ้ง
        if (_current != null && _current.Id == id) return;
        foreach (var p in _queue) if (p.Id == id) return;

        _queue.Add(new Pending
        {
            Id = id,
            Priority = seq.priority,
            Since = Time.unscaledTime,
            Order = _orderCounter++,
            Triggerer = triggerer != NoRole ? triggerer
                      : TryGetPlayerRole(Runner, Runner.LocalPlayer, out var own) ? Encode(own) : (byte)1,
            DuringCutscene = seq.playDuringCutscene,
        });

        if (seq.interruptLowerPriority && _current != null && _current.priority < seq.priority)
            InterruptCurrent();
    }

    /// <summary>
    /// ตัดตัวที่เล่นอยู่ทิ้ง (ไม่กลับมาเล่นต่อ) แล้วเริ่มตัวที่สำคัญสุดในคิวทันที — ไม่รอช่วงเว้นระหว่าง sequence
    /// เครื่องอื่นได้ RPC_Play ตัวใหม่ไปแทนที่เอง · ถ้าเริ่มไม่ได้ (คัตซีนเล่นอยู่) ปิดกล่องทุกเครื่องแล้วรอคิวตามปกติ
    /// </summary>
    private void InterruptCurrent()
    {
        _current = null;      // ไม่ Hide — ตัวใหม่ขึ้นแทนในกล่องทันที ไม่กระพริบ
        _nextStartTime = 0f;
        TickQueue();

        if (_current == null)
        {
            StopLocal();
            RPC_Stop();
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, TickAligned = false)]
    private void RPC_Stop() => StopLocal();

    private void TickQueue()
    {
        if (_queue.Count == 0) return;

        // ระหว่างคัตซีนเริ่มได้เฉพาะบทประกอบคัตซีน — ทับตัวที่หยุดนับค้างอยู่ได้ (StartLocal พักไว้ให้)
        bool cutscene = TimelineController.IsAnyCutscenePlaying;
        if (_current != null && !IsPausedByCutscene) return;

        float now = Time.unscaledTime;
        if (now < _nextStartTime) return;

        _queue.RemoveAll(p => p.Priority == DialoguePriority.Banter && now - p.Since > banterMaxWait);

        int best = -1;
        for (int i = 0; i < _queue.Count; i++)
        {
            if (cutscene && !_queue[i].DuringCutscene) continue;
            if (best < 0 ||
                _queue[i].Priority > _queue[best].Priority ||
                (_queue[i].Priority == _queue[best].Priority && _queue[i].Order < _queue[best].Order))
                best = i;
        }
        if (best < 0) return;

        var next = _queue[best];
        _queue.RemoveAt(best);

        StartLocal(next.Id, next.Triggerer);
        RPC_Play(next.Id, next.Triggerer);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies, TickAligned = false)]
    private void RPC_Play(int id, byte triggerer) => StartLocal(id, triggerer);

    // ───────────────────── ทุกเครื่อง ─────────────────────

    private void Update()
    {
        if (!IsSpawned) return;
        if (HasStateAuthority) TickQueue();
        TickLocal();
    }

    private void StartLocal(int id, byte triggerer)
    {
        if (!DialogueSequence.TryGet(id, out var seq) || seq.lines.Count == 0) return;

        // บทประกอบคัตซีนแทรกตัวที่หยุดนับค้างอยู่ → พักไว้ แล้วเล่นต่อหลังบทนี้จบ
        if (seq.playDuringCutscene && _current != null && !_current.playDuringCutscene && _suspended == null)
        {
            _suspended = _current;
            _suspendedTriggerer = _currentTriggerer;
            _suspendedLine = _lineIndex;
        }

        _current = seq;   // ของเก่าที่ยังไม่จบ (เวลาเหลื่อมกันนิดหน่อย) ถูกแทนที่
        _currentTriggerer = Decode(triggerer);
        _lineIndex = 0;
        BeginLine();
    }

    private void BeginLine()
    {
        if (_current == null) return;

        while (_lineIndex < _current.lines.Count)
        {
            var line = _current.lines[_lineIndex];
            var who = ResolveSpeaker(line.speaker);
            if (!line.TryGetLine(who, out string spoken, out DialogueEmotion face))
            {
                _lineIndex++;
                continue;
            }

            // นับจากตัวที่เห็นจริง (ไม่นับ tag <shake>/<wave>) + ช่วงหยุดที่วรรคตอน — จากต้นฉบับอังกฤษ ทุกเครื่องได้เลขเดียวกัน
            int len = DialogueTextMarkup.VisibleLength(spoken);
            float typeSeconds = DialogueTextMarkup.TypingUnits(spoken) / charsPerSecond;
            _lineRemaining = Mathf.Max(minLineSeconds, typeSeconds + holdBase + holdPerChar * len + line.extraHold);
            if (view != null) view.ShowLine(who, face, spoken, typeSeconds);
            return;
        }

        FinishCurrent();
    }

    private DialogueSpeaker ResolveSpeaker(DialogueSpeaker s)
    {
        if (s != DialogueSpeaker.Triggerer && s != DialogueSpeaker.TriggererPartner) return s;

        bool triggererIsA = _currentTriggerer == PlayerRole.RoleA;
        bool speakerIsA = s == DialogueSpeaker.Triggerer ? triggererIsA : !triggererIsA;
        return speakerIsA ? DialogueSpeaker.Lyra : DialogueSpeaker.Bobby;
    }

    private void TickLocal()
    {
        if (_current == null) return;
        if (IsPausedByCutscene) return;   // หยุดนับ — UI ซ่อนเองระหว่างนี้

        _lineRemaining -= Time.unscaledDeltaTime;
        if (_lineRemaining > 0f) return;

        _lineIndex++;
        BeginLine();
    }

    /// <summary>บรรทัดหมด (หรือถูกข้ามหมด) — กลับไปบทที่พักไว้ หรือปิดกล่องแล้วเว้นช่วงก่อนตัวถัดไป</summary>
    private void FinishCurrent()
    {
        if (_suspended != null)
        {
            ResumeSuspended();
            return;
        }

        StopLocal();
        if (HasStateAuthority) _nextStartTime = Time.unscaledTime + gapBetweenSequences;
    }

    /// <summary>กลับไปเล่นบทที่ถูกพักไว้ — โชว์บรรทัดเดิมใหม่ทั้งบรรทัด (กล่องถูกบทแทรกปิดไปแล้ว)</summary>
    private void ResumeSuspended()
    {
        _current = _suspended;
        _currentTriggerer = _suspendedTriggerer;
        _lineIndex = _suspendedLine;
        _suspended = null;
        BeginLine();
    }

    private void StopLocal()
    {
        _current = null;
        _suspended = null;
        if (view != null) view.Hide();
    }
}
