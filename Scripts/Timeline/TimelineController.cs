using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.UI;
using UnityEngine.Video;
using StarterAssets;
using System.Collections;

/// <summary>
/// แนบไว้ที่ GameObject เดียวกับ PlayableDirector ของ Timeline
/// เมื่อ Timeline เริ่มเล่น จะล็อค Movement ของผู้เล่นทุกคน (Owner)
/// เมื่อ Timeline จบ จะปลดล็อคคืน
/// </summary>
public class TimelineController : MonoBehaviour
{
    private const float ScreenFaderDefaultDurationFallback = 0.75f;

    // ภาพวิดีโอเหลื่อมเสียงหลังเฟรมค้าง — ดู ResyncDriftedVideo
    private const float VideoResyncConfirmSeconds = 0.5f;  // เหลื่อมนานเท่านี้ก่อนค่อย seek (ให้ ExternalTime แก้เองก่อน ปกติ ~0.2 วิ)
    private const float VideoSeekTimeout = 2f;             // seekCompleted ไม่มา → สั่ง seek ใหม่ได้
    private const double VideoSeekLead = 0.1;              // seek จนภาพแรกขึ้น ~0.1–0.13 วิ → เผื่อไว้ ภาพแรกหลัง seek จะตรงกับเสียงพอดี (เกินนี้ภาพนำค้าง)
    private const double VideoEndMargin = 0.5;             // ท้ายคลิปปล่อยหยุดที่เฟรมสุดท้ายตามปกติ
    private const double VideoDriftHysteresis = 0.05;      // นับแล้วเลิกนับเมื่อต่ำกว่าเกณฑ์ลบเท่านี้ — ค่าเหลื่อมแกว่ง ±0.03 ตามจังหวะเฟรม/บัฟเฟอร์เสียง ไม่งั้นนับไม่ครบ

    [Header("References")]
    [SerializeField] private PlayableDirector director;

    [Header("Video Preload (แก้อาการคัตซีนแลค/เสียงนำภาพ)")]
    [Tooltip("VideoPlayer ของคัตซีน — ถ้าเว้นว่างจะหาใต้ object นี้ให้เอง\n" +
             "จะถูก Prepare() ตั้งแต่ซีนโหลด แทนที่จะเพิ่งเริ่มโหลดตอน Timeline เปิด GameObject")]
    [SerializeField] private VideoPlayer cutsceneVideo;

    [Tooltip("Canvas ของคัตซีน — ใช้ปิด/เปิดการแสดงผลแทนการปิด GameObject\n" +
             "(ถ้าปิด GameObject ตัว VideoPlayer จะไม่ทำงาน เตรียมวิดีโอล่วงหน้าไม่ได้)\n" +
             "ถ้าเว้นว่างจะหา Canvas ที่ครอบ VideoPlayer ให้เอง")]
    [SerializeField] private Canvas cutsceneCanvas;

    [Tooltip("รอเตรียมวิดีโอนานสุดกี่วินาทีก่อนยอมเล่นทั้งที่ยังไม่พร้อม (กันค้างถ้าไฟล์มีปัญหา)")]
    [SerializeField, Min(0.1f)] private float videoPrepareTimeout = 5f;

    [Tooltip("ล็อกเวลาวิดีโอเข้ากับเวลาของ Timeline ทุกเฟรม (VideoTimeReference.ExternalTime)\n" +
             "เป็นวิธีที่ Unity ทำมาเพื่อ sync วิดีโอกับ clock ภายนอกโดยเฉพาะ\n" +
             "กันทั้งอาการเหลื่อมคงที่ (latency ตอนเริ่ม) และอาการยิ่งเล่นยิ่งห่าง (clock คนละตัว)")]
    [SerializeField] private bool syncVideoToTimeline = true;

    [Tooltip("ชดเชยเวลาวิดีโอเทียบ Timeline (วินาที)\n" +
             "บวก = ดันภาพให้ไปข้างหน้า (ใช้เมื่อ 'เสียงมาก่อนภาพ')\n" +
             "ลบ  = ดึงภาพให้ช้าลง (ใช้เมื่อ 'ภาพมาก่อนเสียง')\n" +
             "ปกติควรเป็น 0 — ใช้เมื่อตัวไฟล์ mp4/wav เองถูก export มาเหลื่อมกัน")]
    [SerializeField] private float videoTimeOffset = 0f;

    [Tooltip("ภาพเหลื่อมกับเสียง (ตามหลังหรือนำ) ค้างเกินกี่วินาทีถึงจะสั่งกระโดด (seek) ไปตำแหน่งของ Timeline\n" +
             "กันเฟรมค้างสั้นๆ ถี่ๆ (โหลด/spawn/GC) หรือภาพแรกขึ้นช้า สะสมจนภาพตามหลังเสียงค้างอยู่อย่างนั้น\n" +
             "ตอนตรงกันปกติเหลื่อมไม่เกิน ~0.1 วิ — ต่ำกว่า 0.15 จะ seek พร่ำเพรื่อ")]
    [SerializeField, Min(0.15f)] private float videoResyncThreshold = 0.15f;

    private bool _videoPrepareStarted;
    private float _videoDriftSince = -1f;
    private float _videoSeekStartedAt = -99f;
    private bool _videoSeeking;

    [Header("Skip (CutsceneSkipVote)")]
    [Tooltip("เวลา fade ดำก่อนตัดจบคัตซีนตอนผู้เล่นกดข้าม (วินาที)")]
    [SerializeField, Min(0f)] private float skipFadeDuration = 0.3f;

    // มีคัตซีนกำลังเล่นอยู่กี่ตัว — ระบบที่รับปุ่ม E (สวิช/ป้าย/คอนโซล) ใช้เช็คเพื่อไม่ให้ทำงานระหว่างคัตซีน
    // (ตัวละครถูกล็อกแค่การเดิน ไม่ได้ล็อกการกด interact และ E ใช้กดค้างเพื่อข้ามคัตซีนด้วย)
    private static int s_playingCount;
    public static bool IsAnyCutscenePlaying => s_playingCount > 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => s_playingCount = 0;

    private bool _countedPlaying;
    private bool _skipRequested;
    private bool _skipped;
    private Coroutine _playCo;

    /// <summary>คัตซีนกำลังเล่นอยู่ และยังไม่ถูกสั่งข้าม</summary>
    public bool IsPlaying => director != null && director.state == PlayState.Playing && !_skipRequested;

    [Header("Tutorial UI (optional)")]
    [Tooltip("ถ้าเปิด จะโชว์ Tutorial UI หลัง Timeline จบ (เฉพาะ local client)")]
    [SerializeField] private bool showTutorialOnTimelineEnd = false;

    [Tooltip("Tutorial ของผู้เล่น A (แนะนำ: Host/ClientId=0)")]
    [SerializeField] private InteractableSignPanel tutorialPanelPlayerA;

    [Tooltip("Tutorial ของผู้เล่น B (แนะนำ: Client/ClientId=1)")]
    [SerializeField] private InteractableSignPanel tutorialPanelPlayerB;

    [Tooltip("ดีเลย์ก่อนโชว์ Tutorial หลัง Timeline stopped (กันจังหวะ UI กระพริบ/พร้อมกันหลายอัน)")]
    [SerializeField, Min(0f)] private float tutorialShowDelay = 0f;

    [Header("Options")]
    [Tooltip("ล็อคกล้องด้วยหรือไม่ (ถ้า true ผู้เล่นจะหมุนกล้องไม่ได้ระหว่าง Intro)")]
    [SerializeField] private bool lockCamera = true;
    
    [Tooltip("ถ้า true จะล็อค movement ตั้งแต่เริ่มซีน และจะคงล็อคไว้จนกว่า Timeline จะหยุด (stopped) เพื่อกันช่วงที่ผู้เล่น/Client spawn ช้า")]
    [SerializeField] private bool lockFromSceneStart = true;

    [Tooltip("ถ้า true จะปิดเสียง SFX/BGM ระหว่าง Timeline เล่น (local)")]
    [SerializeField] private bool muteSfxAndBgmDuringTimeline = true;

    [Tooltip("ถ้า true จะปิดเสียง SFX/BGM ตั้งแต่เริ่มซีน และคงไว้จนกว่า Timeline จะหยุด (stopped) เพื่อกันช่วงรอผู้เล่น/Client spawn")]
    [SerializeField] private bool muteFromSceneStart = true;

    public enum FadeMode
    {
        None = 0,
        FadeIn = 1,
        FadeOut = 2
    }

    [Tooltip("Fade ตอน Timeline จบ (ใช้กับทั้งกรณีปกติและก่อนโหลดซีน)")]
    [SerializeField] private FadeMode fadeOnTimelineEnd = FadeMode.None;

    [Tooltip("ระยะเวลา Fade (ถ้า <= 0 จะใช้ค่า default ใน ScreenFader)")]
    [SerializeField] private float fadeDuration = -1f;

    [Tooltip("ถ้า true จะเปลี่ยนซีนเมื่อ Timeline จบ (เรียกผ่าน LoadingScreenManager)")]
    [SerializeField] private bool loadSceneOnTimelineEnd = false;

    [Tooltip("ชื่อซีนปลายทาง (ถ้าเว้นว่างจะไม่ทำอะไร)")]
    [SerializeField] private string targetSceneName = "";

    [Tooltip("ถ้า true จะทำ Fade (ตาม FadeMode) แล้วรอให้จบก่อนค่อย LoadScene")]
    [SerializeField] private bool fadeBeforeLoadScene = false;

    [Tooltip("ถ้า true จะเริ่ม Fade ก่อน Timeline ใกล้จบ (ตาม FadeMode)")]
    [SerializeField] private bool fadeNearTimelineEnd = false;

    [Tooltip("จำนวนวินาทีก่อนจบ Timeline ที่จะเริ่ม Fade")]
    [SerializeField, Min(0f)] private float fadeLeadTime = 1f;

    [Header("Events")]
    [Tooltip("ยิงในเครื่องตัวเองเมื่อ Timeline เล่นไปถึง Delay Start — ใช้เล่นบทพูดประกอบคัตซีน\n" +
             "(DialogueTrigger EventOnly → Play + ติ๊ก Play During Cutscene ใน DialogueSequence ไม่งั้นบทรอคัตซีนจบ)\n" +
             "ถูกข้ามก่อนถึงเวลา = ไม่ยิง")]
    [SerializeField] private UnityEngine.Events.UnityEvent onTimelineStarted;

    [Tooltip("ยิง On Timeline Started เมื่อ Timeline เล่นไปกี่วินาที (นับตามเวลาของ Timeline — 0 = ทันทีที่เริ่ม)")]
    [SerializeField, Min(0f)] private float delayStart = 0f;

    [Tooltip("ยิงในเครื่องตัวเองตอน Timeline จบ — ทั้งเล่นจบเองและถูกข้าม (ไม่ยิงถ้าตั้งให้โหลดซีนต่อ)\n" +
             "ใช้ต่อคัตซีนเป็นทอด: ผูกไป PlayTimeline ของคัตซีนถัดไป (ตัวถัดไปควรเปิด Lock From Scene Start กันช่วงหลุดล็อก)")]
    [SerializeField] private UnityEngine.Events.UnityEvent onTimelineFinished;

    private bool _startedEventPending;
    private bool _shouldLock;
    private bool? _lastAppliedLock;
    private bool _shouldMute;
    private bool _lastAppliedMute;
    private bool _sceneLoadRequested;
    private Coroutine _loadSceneCo;
    private bool _nearEndFadeTriggered;
    private float _fadeStartUnscaledTime;
    private float _fadePlannedDuration;
    private Coroutine _showTutorialCo;

    private void Awake()
    {
        if (director == null)
            director = GetComponent<PlayableDirector>();

        _shouldLock = lockFromSceneStart;
        _shouldMute = muteFromSceneStart;

        // ปิดเสียงให้เร็วที่สุด (กัน BGM/SFX หลุดมา 1 เฟรมแรก)
        if (_shouldMute)
            ApplyMuteIfNeeded(true);

        BeginVideoPreload();
    }

    // ───────────────────── Video Preload ─────────────────────

    /// <summary>
    /// เริ่มโหลด/ถอดรหัสวิดีโอตั้งแต่ซีนเริ่ม แทนที่จะรอให้ ActivationTrack เปิด GameObject
    ///
    /// ปัญหาเดิม: Timeline มีแค่ ActivationTrack + AudioTrack (ไม่มี VideoTrack)
    /// พอ Play() ที่ t=0 เสียงเริ่มทันที แต่ VideoPlayer เพิ่งตื่นแล้วเริ่มเปิดไฟล์ 64MB
    /// + init decoder ตอนนั้น -> ภาพเริ่มช้ากว่าเสียง แล้ว skipOnDrop ทำให้ทิ้งเฟรมไล่ตาม = กระตุก
    /// </summary>
    private void BeginVideoPreload()
    {
        if (_videoPrepareStarted) return;

        if (cutsceneVideo == null)
            cutsceneVideo = GetComponentInChildren<VideoPlayer>(true);
        if (cutsceneVideo == null) return;

        if (cutsceneCanvas == null)
            cutsceneCanvas = cutsceneVideo.GetComponentInParent<Canvas>(true);

        _videoPrepareStarted = true;

        // ต้องเปิด GameObject ให้ครบสายก่อน ไม่งั้น VideoPlayer ไม่ทำงานและ Prepare() ไม่เดิน
        //
        // สำคัญ: ซ่อนด้วยการปิด "Canvas component" ไม่ใช่ปิด GameObject
        // เพราะใน Canvas มี "Black Screen" ที่ active อยู่ — ถ้าเปิด Canvas ทิ้งไว้
        // จอจะดำทับเกมทั้งเลเวล (เห็นชัดกับ Outro ที่เล่นตอนท้ายเลเวล)
        // ปิดที่ Canvas component ทำให้ไม่มีอะไรถูกวาด แต่ VideoPlayer ยังทำงานได้
        if (cutsceneCanvas != null) cutsceneCanvas.enabled = false;

        var go = cutsceneVideo.gameObject;
        if (!go.activeSelf) go.SetActive(true);
        for (var p = go.transform.parent; p != null && p != transform; p = p.parent)
            if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);

        // อย่าให้เล่นเองตอน enable — เราจะสั่งเล่นพร้อม Timeline
        cutsceneVideo.playOnAwake = false;
        cutsceneVideo.seekCompleted += OnVideoSeekCompleted;

        // ห้ามข้ามเฟรมไล่เวลา = ต้นเหตุ "ภาพค้างแต่เสียงเดิน": เฟรมค้าง (hitch) ระหว่างคัตซีน Timeline เดินด้วย DSP clock ต่อ
        // → VideoPlayer ไล่ตามด้วยการทิ้งเฟรม ซึ่งค้างภาพเดิม 0.1–8 วิแบบสุ่ม บางรอบค้างจนจบ หรือเลยไปนำเสียงค้างอย่างนั้น
        // ปิดแล้ว ExternalTime กระโดดไปตำแหน่งของ Timeline เองใน ~0.2 วิ (วัดใน Editor 2026-10-14 ทั้งไฟล์ใหม่/baseline เก่า)
        // ไม่เช็ค canSetSkipOnDrop — ก่อน Prepare() มันเป็น false ทั้งที่ตั้งค่าได้จริง
        cutsceneVideo.skipOnDrop = false;

        if (syncVideoToTimeline)
        {
            // ให้ VideoPlayer ยึด "เวลาที่เราป้อนให้" เป็นหลัก แทนการเดินนาฬิกาของตัวเอง
            // แล้วเราจะป้อน director.time ให้ทุกเฟรมใน Update()
            cutsceneVideo.timeReference = VideoTimeReference.ExternalTime;
            cutsceneVideo.externalReferenceTime = 0d;
        }

        cutsceneVideo.Prepare();
    }

    /// <summary>
    /// ป้อนเวลาของ Timeline ให้ VideoPlayer ทุกเฟรม
    ///
    /// AudioTrack ของคัตซีนอยู่บน Timeline อยู่แล้ว ดังนั้น director.time คือ "ตำแหน่งเสียง"
    /// พอบังคับให้ภาพอ้างเวลาเดียวกัน ภาพจะถูกลากให้ตรงกับเสียง ไม่ว่าจะเริ่มเหลื่อมหรือเฟรมตก
    /// (ต้องปิด skipOnDrop — ดู BeginVideoPreload · ส่วนที่ ExternalTime ไม่แก้ → ResyncDriftedVideo)
    /// </summary>
    private void SyncVideoToDirector()
    {
        if (!syncVideoToTimeline) return;
        if (cutsceneVideo == null || director == null) return;
        if (director.state != PlayState.Playing) return;

        cutsceneVideo.externalReferenceTime = director.time + videoTimeOffset;
    }

    /// <summary>
    /// ตาข่ายรองรับภาพเหลื่อมเสียงที่ ExternalTime ไม่แก้ให้ (เหลื่อมไม่ถึง ~0.5 วิมันปล่อยค้างไว้):
    /// hitch สั้นๆ ถี่ๆ (0.3 วิ × หลายครั้ง) สะสมจนตามหลัง 0.4–0.7 วิ · ภาพแรกขึ้นช้า ตามหลัง ~0.2 วิไปทั้งคัตซีน
    /// (hitch ยาวทีเดียว ExternalTime กระโดดเองใน ~0.2 วิ) · เหลื่อมเกิน videoResyncThreshold นานพอ = seek ไปตำแหน่งของ Timeline (~0.1 วิ)
    /// </summary>
    private void ResyncDriftedVideo()
    {
        if (!syncVideoToTimeline || cutsceneVideo == null || director == null) return;
        if (director.state != PlayState.Playing || _skipRequested || !cutsceneVideo.isPlaying)
        {
            _videoDriftSince = -1f;
            return;
        }

        // seek ก่อนหน้ายังไม่เสร็จ — สั่งซ้ำ = ยกเลิกอันเดิม
        float now = Time.unscaledTime;
        if (_videoSeeking && now - _videoSeekStartedAt < VideoSeekTimeout) return;

        double target = director.time + videoTimeOffset;
        // เทียบกลางช่วงของเฟรมที่โชว์อยู่ (time = ต้นเฟรม) ไม่งั้นภาพตรงเสียงก็วัดได้ตามหลังเฉลี่ยครึ่งเฟรม
        double frameMid = cutsceneVideo.frameRate > 0f ? 0.5 / cutsceneVideo.frameRate : 0d;
        double drift = cutsceneVideo.time + frameMid - target; // ลบ = ภาพตามหลัง (ค้าง) · บวก = ภาพนำเสียง
        double threshold = _videoDriftSince < 0f ? videoResyncThreshold : videoResyncThreshold - VideoDriftHysteresis;
        if (System.Math.Abs(drift) < threshold || !cutsceneVideo.canSetTime
            || target + VideoSeekLead >= cutsceneVideo.length - VideoEndMargin)
        {
            _videoDriftSince = -1f;
            return;
        }

        if (_videoDriftSince < 0f) _videoDriftSince = now;
        if (now - _videoDriftSince < VideoResyncConfirmSeconds) return;

        Debug.LogWarning($"[TimelineController] ภาพวิดีโอเหลื่อมเสียง {drift:+0.00;-0.00}s → seek ไป {target:F2}s", this);
        _videoDriftSince = -1f;
        _videoSeeking = true;
        _videoSeekStartedAt = now;
        cutsceneVideo.time = target + VideoSeekLead;
    }

    private void OnVideoSeekCompleted(VideoPlayer source) => _videoSeeking = false;

    /// <summary>
    /// ผูกกับ CoopSceneStarter.OnSceneStartSynced แทนการเรียก PlayableDirector.Play() ตรงๆ
    /// จะรอให้วิดีโอพร้อมก่อน แล้วค่อยให้ภาพกับเสียงเริ่มพร้อมกัน
    /// </summary>
    public void PlayTimeline()
    {
        if (director == null) director = GetComponent<PlayableDirector>();
        if (director == null) return;

        // ไม่มีวิดีโอในคัตซีนนี้ -> เล่นเลยเหมือนเดิม
        if (cutsceneVideo == null)
        {
            director.Play();
            if (_skipRequested) FinishSkip();
            return;
        }

        if (_playCo != null) StopCoroutine(_playCo);
        _playCo = StartCoroutine(PlayWhenVideoReady_Co());
    }

    // ───────────────────── Skip ─────────────────────

    /// <summary>
    /// ข้ามคัตซีน: fade ดำสั้นๆ แล้วกระโดดไปท้าย Timeline + Stop()
    /// event stopped จะยิงตามปกติ → ปลดล็อก/คืนเสียง/fade/tutorial/โหลดซีน ทำงานเหมือนเล่นจบเอง
    /// เรียกจาก CutsceneSkipVote บนทุกเครื่องพร้อมกัน — ถ้าเครื่องนี้ยังไม่เริ่มเล่น จะข้ามทันทีที่เริ่ม
    /// </summary>
    public void SkipToEnd()
    {
        if (_skipRequested || _skipped) return;
        _skipRequested = true;

        if (director == null) director = GetComponent<PlayableDirector>();
        if (director == null || director.state != PlayState.Playing) return; // รอ PlayTimeline

        StartCoroutine(SkipAfterFade_Co());
    }

    private IEnumerator SkipAfterFade_Co()
    {
        if (skipFadeDuration > 0f && ScreenFader.I != null)
        {
            ScreenFader.I.FadeOut(skipFadeDuration);
            yield return new WaitForSecondsRealtime(skipFadeDuration);
        }
        FinishSkip();
    }

    private void FinishSkip()
    {
        if (_skipped) return;
        _skipped = true;

        if (_playCo != null) { StopCoroutine(_playCo); _playCo = null; }
        if (cutsceneVideo != null) cutsceneVideo.Stop();
        if (director == null) return;

        // เผื่อ coroutine เตรียมวิดีโอยังไม่ได้สั่งเล่น — ต้อง Play ก่อนตั้งค่า fade เพราะ OnTimelinePlayed รีเซ็ตค่า
        if (director.state != PlayState.Playing) director.Play();

        if (fadeOnTimelineEnd == FadeMode.FadeOut)
        {
            // จอดำอยู่แล้ว = สภาพตอนจบของคัตซีนนี้ → ไม่ต้อง fade/รอซ้ำ (ServerLoadSceneAfterFadeOut โหลดต่อทันที)
            _nearEndFadeTriggered = true;
            _fadePlannedDuration = 0f;
            _fadeStartUnscaledTime = Time.unscaledTime;
        }
        else
        {
            // fade ใกล้จบอาจยิงไปแล้วก่อนเรากด fade ดำทับ → ให้ ApplyFadeIfNeeded เปิดจอใหม่
            _nearEndFadeTriggered = false;
        }

        director.time = director.duration;
        director.Evaluate();
        director.Stop(); // → OnTimelineStopped
    }

    private IEnumerator PlayWhenVideoReady_Co()
    {
        BeginVideoPreload();

        float deadline = Time.realtimeSinceStartup + videoPrepareTimeout;
        while (cutsceneVideo != null && !cutsceneVideo.isPrepared && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (cutsceneVideo != null && !cutsceneVideo.isPrepared)
            Debug.LogWarning($"[TimelineController] วิดีโอยังเตรียมไม่เสร็จใน {videoPrepareTimeout}s — เล่นต่อไปเลย", this);

        // คืนการแสดงผลให้ Canvas ก่อนเริ่มเล่น
        if (cutsceneCanvas != null) cutsceneCanvas.enabled = true;

        if (cutsceneVideo == null)
        {
            director.Play();
            _playCo = null;
            if (_skipRequested) FinishSkip();
            yield break;
        }

        // เริ่มทั้งคู่ — ไม่ต้องพยายามจัดให้เริ่มพร้อมกันเป๊ะแล้ว
        // เพราะ SyncVideoToDirector() จะลากเวลาวิดีโอให้ตรงกับ Timeline ทุกเฟรมอยู่แล้ว
        if (syncVideoToTimeline)
            cutsceneVideo.externalReferenceTime = videoTimeOffset;

        cutsceneVideo.Play();
        director.Play();
        _playCo = null;

        if (_skipRequested) FinishSkip();
    }

    private void OnEnable()
    {
        if (_shouldMute)
            ApplyMuteIfNeeded(true);

        if (director != null)
        {
            director.played += OnTimelinePlayed;
            director.stopped += OnTimelineStopped;

            // กันกรณีเริ่มซีนแล้ว Timeline เล่นไปแล้ว (หรือมี 1 เฟรมก่อน event played ถูกยิง)
            if (director.state == PlayState.Playing)
            {
                _shouldLock = true;
                ApplyMuteIfNeeded(true);
            }
        }
    }

    private void OnDisable()
    {
        if (director != null)
        {
            director.played -= OnTimelinePlayed;
            director.stopped -= OnTimelineStopped;
        }

        // กันกรณี object ถูก disable ระหว่าง Timeline เล่น แล้วเสียงค้าง mute
        ApplyMuteIfNeeded(false);
        SetCountedPlaying(false);

        if (_showTutorialCo != null)
        {
            StopCoroutine(_showTutorialCo);
            _showTutorialCo = null;
        }

        if (_loadSceneCo != null)
        {
            StopCoroutine(_loadSceneCo);
            _loadSceneCo = null;
        }
    }

    private void Start()
    {
        // กันอาการ Host เดินได้ก่อน Client spawn:
        // ล็อคตั้งแต่เริ่มซีน (ถ้าเปิด option) แล้วคงล็อคไว้จน Timeline stopped
        if (lockFromSceneStart)
            _shouldLock = true;

        if (muteFromSceneStart)
            _shouldMute = true;
    }
    
    private void Update()
    {
        if (_shouldLock) ApplyLockIfNeeded(true);
        if (_shouldMute) ApplyMuteIfNeeded(true);
        TryFadeNearTimelineEnd();
        SyncVideoToDirector();
        ResyncDriftedVideo();
        TryInvokeStartedEvent();
    }

    // นับตาม director.time — เฟรมตก/เริ่มช้า ยังตรงกับภาพบน Timeline
    private void TryInvokeStartedEvent()
    {
        if (!_startedEventPending || _skipRequested) return;
        if (director == null || director.state != PlayState.Playing) return;
        if (director.time < delayStart) return;

        _startedEventPending = false;
        onTimelineStarted?.Invoke();
    }

    private void OnTimelinePlayed(PlayableDirector pd)
    {
        _shouldLock = true;
        ApplyLockIfNeeded(true);
        _shouldMute = true;
        ApplyMuteIfNeeded(true);
        _nearEndFadeTriggered = false;
        _startedEventPending = true;
        SetCountedPlaying(true);
    }

    private void SetCountedPlaying(bool playing)
    {
        if (_countedPlaying == playing) return;
        _countedPlaying = playing;
        s_playingCount += playing ? 1 : -1;
    }

    private void OnTimelineStopped(PlayableDirector pd)
    {
        _startedEventPending = false;
        SetCountedPlaying(false);
        _shouldLock = false;
        ApplyLockIfNeeded(false);
        _shouldMute = false;
        ApplyMuteIfNeeded(false);
        bool didLoadScene = ApplySceneLoadIfNeeded();
        if (!didLoadScene)
        {
            // ข้ามมาแล้วจอดำค้างจาก SkipAfterFade_Co — คัตซีนที่ไม่ได้ตั้ง fade ตอนจบต้องเปิดจอคืนเอง
            if (_skipped && fadeOnTimelineEnd == FadeMode.None && ScreenFader.I != null)
                ScreenFader.I.FadeIn(skipFadeDuration);

            ApplyFadeIfNeeded();
            TryShowTutorialForLocalPlayer();
            onTimelineFinished?.Invoke();
        }
    }

    private bool ApplySceneLoadIfNeeded()
    {
        if (!loadSceneOnTimelineEnd) return false;
        if (string.IsNullOrEmpty(targetSceneName)) return false;
        // เฉพาะ scene authority (Fusion Shared Mode = master client) เป็นคนสั่งโหลดซีน
        if (!FusionSession.IsSceneAuthority) return false;
        if (_sceneLoadRequested) return true;

        _sceneLoadRequested = true;
        _loadSceneCo = StartCoroutine(ServerLoadSceneAfterFadeOut());
        return true;
    }

    private IEnumerator ServerLoadSceneAfterFadeOut()
    {
        if (fadeBeforeLoadScene && fadeOnTimelineEnd != FadeMode.None && ScreenFader.I != null)
        {
            float dur = GetFadeDurationForWaiting();

            if (!_nearEndFadeTriggered)
            {
                TriggerFadeNow();
            }

            float elapsed = Time.unscaledTime - _fadeStartUnscaledTime;
            float remain = Mathf.Max(0f, _fadePlannedDuration - elapsed);
            if (remain > 0f)
                yield return new WaitForSecondsRealtime(remain);
        }

        LoadingScreenManager.LoadSceneNetworked(targetSceneName);
        _loadSceneCo = null;
    }

    private void TryShowTutorialForLocalPlayer()
    {
        if (!showTutorialOnTimelineEnd) return;

        // Tutorial UI เป็น local UI: ต้องอยู่ใน session เพื่อรู้ว่าเราเป็นใคร (A/B)
        if (FusionSession.ActiveRunner == null) return;

        InteractableSignPanel panel = GetTutorialPanelForThisClient();
        if (panel == null) return;

        if (_showTutorialCo != null) StopCoroutine(_showTutorialCo);
        _showTutorialCo = StartCoroutine(ShowTutorialAfterDelayCo(panel, tutorialShowDelay));
    }

    private InteractableSignPanel GetTutorialPanelForThisClient()
    {
        // ใช้ระบบ Role จาก Lobby (PlayerRoleFromLobby) เพื่อ map A/B ให้ถูกต้อง
        var localPlayer = FusionSession.LocalPlayerObject;
        if (localPlayer != null)
        {
            var roleComp = localPlayer.GetComponent<PlayerRoleFromLobby>();
            if (roleComp != null)
            {
                return roleComp.Role == PlayerRole.RoleA ? tutorialPanelPlayerA : tutorialPanelPlayerB;
            }
        }

        // fallback: ถ้าหา role ไม่เจอ ใช้ slot ที่เลือกไว้ (0 = A) ถ้าไม่มีให้ master = A, อีกคน = B
        // (เดิม NGO: Host(0)=A, Client(1)=B)
        bool isA = FusionSession.LocalSlot != FusionSession.NoSlot
            ? FusionSession.LocalSlot == 0
            : FusionSession.ActiveRunner != null && FusionSession.ActiveRunner.IsSharedModeMasterClient;
        return isA ? tutorialPanelPlayerA : tutorialPanelPlayerB;
    }

    private static IEnumerator ShowTutorialAfterDelayCo(InteractableSignPanel panel, float delaySeconds)
    {
        if (delaySeconds > 0f)
            yield return new WaitForSecondsRealtime(delaySeconds);

        // ใช้ระบบเปิด/ปิดเดียวกับป้าย เพื่อให้ปุ่มปิดเรียก ClosePanel() ได้เหมือนเดิม
        if (panel != null && panel.isActiveAndEnabled)
            panel.OpenPanelExternal();
    }

    private void ApplyFadeIfNeeded()
    {
        if (fadeOnTimelineEnd == FadeMode.None) return;
        if (ScreenFader.I == null) return;

        if (_nearEndFadeTriggered) return;
        TriggerFadeNow();
    }

    private void TryFadeNearTimelineEnd()
    {
        if (!fadeNearTimelineEnd) return;
        if (_nearEndFadeTriggered) return;
        if (fadeOnTimelineEnd == FadeMode.None) return;
        if (director == null) return;
        if (director.state != PlayState.Playing) return;
        if (ScreenFader.I == null) return;

        double dur = director.duration;
        if (double.IsNaN(dur) || double.IsInfinity(dur) || dur <= 0) return;

        double remaining = dur - director.time;
        if (remaining <= fadeLeadTime)
            TriggerFadeNow();
    }

    private float GetFadeDurationForWaiting()
    {
        float dur = fadeDuration;
        if (dur <= 0f) dur = ScreenFaderDefaultDurationFallback;
        return dur;
    }

    private void TriggerFadeNow()
    {
        _fadePlannedDuration = GetFadeDurationForWaiting();
        _fadeStartUnscaledTime = Time.unscaledTime;
        _nearEndFadeTriggered = true;

        float paramDuration = fadeDuration <= 0f ? -1f : fadeDuration;
        if (fadeOnTimelineEnd == FadeMode.FadeOut)
        {
            ScreenFader.I.FadeOut(paramDuration);
        }
        else if (fadeOnTimelineEnd == FadeMode.FadeIn)
        {
            ScreenFader.I.InstantBlack();
            ScreenFader.I.FadeIn(paramDuration);
        }
    }

    private void ApplyMuteIfNeeded(bool muted)
    {
        if (!muteSfxAndBgmDuringTimeline) return;
        if (_lastAppliedMute == muted) return;

        if (SoundMixerManager.Instance != null)
        {
            if (muted) SoundMixerManager.Instance.PushCutsceneMute();
            else SoundMixerManager.Instance.PopCutsceneMute();
        }

        _lastAppliedMute = muted;
    }

    private void ApplyLockIfNeeded(bool locked)
    {
        if (_lastAppliedLock.HasValue && _lastAppliedLock.Value == locked)
        {
            // ยังต้อง enforce ตอนกำลังล็อค เพื่อครอบคลุมผู้เล่นที่ spawn ทีหลัง
            if (!locked) return;
        }

        var players = FindObjectsByType<ThirdPersonController_Rigidbody>(FindObjectsSortMode.None);

        foreach (var player in players)
        {
            // ล็อค/ปลดล็อคเฉพาะผู้เล่นที่เป็น Owner (ตัวเราเอง)
            if (!player.IsOwner) continue;

            // ใช้เมธอดเพื่อให้รีเซ็ต velocity ตอนล็อค (กัน "กดเดินค้าง" ในเฟรมแรก)
            player.SetMovementLocked(locked);

            if (lockCamera)
                player.LockCameraPosition = locked;
        }

        if (!_lastAppliedLock.HasValue || _lastAppliedLock.Value != locked)
        {
            Debug.Log($"[Timeline] Players movement {(locked ? "LOCKED" : "UNLOCKED")}");
            _lastAppliedLock = locked;
        }
    }
}
