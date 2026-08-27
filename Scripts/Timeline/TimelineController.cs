using UnityEngine;
using UnityEngine.Playables;
using StarterAssets;
using Unity.Netcode;
using System.Collections;

/// <summary>
/// แนบไว้ที่ GameObject เดียวกับ PlayableDirector ของ Timeline
/// เมื่อ Timeline เริ่มเล่น จะล็อค Movement ของผู้เล่นทุกคน (Owner)
/// เมื่อ Timeline จบ จะปลดล็อคคืน
/// </summary>
public class TimelineController : MonoBehaviour
{
    private const float ScreenFaderDefaultDurationFallback = 0.75f;

    [Header("References")]
    [SerializeField] private PlayableDirector director;

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
    }

    private void OnTimelinePlayed(PlayableDirector pd)
    {
        _shouldLock = true;
        ApplyLockIfNeeded(true);
        _shouldMute = true;
        ApplyMuteIfNeeded(true);
        _nearEndFadeTriggered = false;
    }

    private void OnTimelineStopped(PlayableDirector pd)
    {
        _shouldLock = false;
        ApplyLockIfNeeded(false);
        _shouldMute = false;
        ApplyMuteIfNeeded(false);
        bool didLoadScene = ApplySceneLoadIfNeeded();
        if (!didLoadScene)
        {
            ApplyFadeIfNeeded();
            TryShowTutorialForLocalPlayer();
        }
    }

    private bool ApplySceneLoadIfNeeded()
    {
        if (!loadSceneOnTimelineEnd) return false;
        if (string.IsNullOrEmpty(targetSceneName)) return false;
        if (NetworkManager.Singleton == null) return false;
        if (!NetworkManager.Singleton.IsServer) return false;
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

        // Tutorial UI เป็น local UI: ต้องมี NetworkManager เพื่อรู้ว่าเราเป็นใคร (A/B)
        if (NetworkManager.Singleton == null) return;

        InteractableSignPanel panel = GetTutorialPanelForThisClient();
        if (panel == null) return;

        if (_showTutorialCo != null) StopCoroutine(_showTutorialCo);
        _showTutorialCo = StartCoroutine(ShowTutorialAfterDelayCo(panel, tutorialShowDelay));
    }

    private InteractableSignPanel GetTutorialPanelForThisClient()
    {
        // ใช้ระบบ Role จาก Lobby (PlayerRoleFromLobby) เพื่อ map A/B ให้ถูกต้อง
        var localPlayerNo = NetworkManager.Singleton.LocalClient?.PlayerObject;
        if (localPlayerNo != null)
        {
            var roleComp = localPlayerNo.GetComponent<PlayerRoleFromLobby>();
            if (roleComp != null)
            {
                return roleComp.Role.Value == PlayerRole.RoleA ? tutorialPanelPlayerA : tutorialPanelPlayerB;
            }
        }

        // fallback: ถ้าหา role ไม่เจอ ให้ map แบบง่าย Host(0)=A, Client(1)=B
        ulong id = NetworkManager.Singleton.LocalClientId;
        return id == 0 ? tutorialPanelPlayerA : tutorialPanelPlayerB;
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
