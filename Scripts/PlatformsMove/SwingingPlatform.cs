using UnityEngine;
using Unity.Netcode;
using DG.Tweening;
using Unity.Netcode.Components; // ใส่ NetworkTransform ใน Inspector ด้วย
using UnityEngine.Audio;

[RequireComponent(typeof(NetworkObject))]
public class SwingingPlatform : NetworkBehaviour
{
    public enum Axis { X, Z }

    [Header("Swing Settings")]
    public Axis swingAxis = Axis.Z;     // เลือกแกนหมุน (แกว่งซ้ายขวา = ส่วนใหญ่ Z)
    public float swingAngle = 45f;      // องศาจากจุดกลางไปแต่ละด้าน
    public float duration = 1.5f;       // เวลาไปถึงสุดข้างหนึ่ง
    public float startDelay = 0f;       // หน่วงก่อนเริ่มแกว่ง (วินาที) เผื่อให้แต่ละตัวไม่พร้อมกัน
    public bool reverseStart = false;   // กลับด้านการแกว่ง (เริ่มไปฝั่งตรงข้าม)
    public Ease ease = Ease.InOutSine;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงตอนถึงสุดฝั่ง A (ฝั่งแรกที่แกว่งไปถึง)")]
    [SerializeField] private AudioClip returnSound;
    [Tooltip("เสียงตอนถึงสุดฝั่ง B (อีกฝั่งหนึ่ง)")]
    [SerializeField] private AudioClip endedSound;
    [Tooltip("เสียงตอนแกว่งผ่าน 'ตรงกลาง' (ตัดผ่านมุมเริ่มต้น)")]
    [SerializeField] private AudioClip centerSwingSound;
    [Tooltip("กันเด้ง: ระยะใกล้จุดกลางที่ยอมให้ trigger (องศา)")]
    [SerializeField, Min(0f)] private float centerCrossDeadzoneDeg = 0.25f;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    private Sequence _seq;
    private Tween _loopTween;
    private Vector3 _startLocalEuler;
    private bool _nextReachIsB;
    private int _lastSideSign; // -1/0/+1 เทียบกับจุดกลาง

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        InitializeAudio();
        _startLocalEuler = transform.localEulerAngles;

        // มุมเป้าหมายสองฝั่ง: +angle และ -angle รอบ "แกนที่เลือก" โดยมีจุดกลาง = มุมเริ่มต้น
        Vector3 plus = _startLocalEuler;
        Vector3 minus = _startLocalEuler;
        if (swingAxis == Axis.X)
        {
            plus.x += Mathf.Abs(swingAngle);
            minus.x -= Mathf.Abs(swingAngle);
        }
        else // Z
        {
            plus.z += Mathf.Abs(swingAngle);
            minus.z -= Mathf.Abs(swingAngle);
        }

        // สลับทิศถ้า reverseStart เปิดอยู่
        Vector3 first  = reverseStart ? minus : plus;
        Vector3 second = reverseStart ? plus  : minus;

        // หน่วง + แกว่งครึ่งจังหวะแรก (กลาง → first)
        // แล้วเริ่มลูปเต็มจังหวะ (first ↔ second) ตลอดไป
        _seq = DOTween.Sequence();
        if (startDelay > 0f) _seq.AppendInterval(startDelay);
        _seq.Append(
            transform.DOLocalRotate(first, duration, RotateMode.Fast)
                .SetEase(Ease.OutSine)
                .OnComplete(() =>
                {
                    // ถึงสุดฝั่ง A ครั้งแรก
                    PlaySwingReachClientRpc(reachedB: false);
                }));
        _seq.OnComplete(() =>
        {
            _nextReachIsB = true; // เริ่มจาก A → B ก่อน
            _lastSideSign = GetSideSign();
            _loopTween = transform
                .DOLocalRotate(second, duration * 2f, RotateMode.Fast)
                .SetEase(ease)
                .SetLoops(-1, LoopType.Yoyo)
                .OnUpdate(CheckCenterCross)
                .OnStepComplete(() =>
                {
                    // Yoyo จะสลับ A/B ทุก step complete
                    PlaySwingReachClientRpc(reachedB: _nextReachIsB);
                    _nextReachIsB = !_nextReachIsB;
                });
        });
    }

    private void InitializeAudio()
    {
        if (audioSource == null) return;
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        if (outputMixerGroup != null)
        {
            audioSource.outputAudioMixerGroup = outputMixerGroup;
        }
    }

    [ClientRpc]
    private void PlaySwingReachClientRpc(bool reachedB)
    {
        var clip = reachedB ? endedSound : returnSound;
        if (clip == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip);
            return;
        }

        AudioSource.PlayClipAtPoint(clip, transform.position);
    }

    private void CheckCenterCross()
    {
        // ตรวจจับตอนมุมหมุน "ตัดผ่าน" จุดกลาง (มุมเริ่มต้น)
        int signNow = GetSideSign();
        if (signNow == 0) return; // อยู่ใน deadzone กลาง ๆ ยังไม่ตัดสินทิศ
        if (_lastSideSign == 0) { _lastSideSign = signNow; return; }

        if (signNow != _lastSideSign)
        {
            _lastSideSign = signNow;
            PlayCenterSwingSoundClientRpc();
        }
    }

    private int GetSideSign()
    {
        // ใช้ DeltaAngle กันปัญหา euler 0..360 wrap
        float delta = 0f;
        Vector3 e = transform.localEulerAngles;
        if (swingAxis == Axis.X)
            delta = Mathf.DeltaAngle(_startLocalEuler.x, e.x);
        else
            delta = Mathf.DeltaAngle(_startLocalEuler.z, e.z);

        if (Mathf.Abs(delta) <= centerCrossDeadzoneDeg) return 0;
        return delta > 0f ? 1 : -1;
    }

    [ClientRpc]
    private void PlayCenterSwingSoundClientRpc()
    {
        if (centerSwingSound == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(centerSwingSound);
            return;
        }

        AudioSource.PlayClipAtPoint(centerSwingSound, transform.position);
    }

    public override void OnNetworkDespawn()
    {
        if (_seq != null && _seq.IsActive()) _seq.Kill();
        if (_loopTween != null && _loopTween.IsActive()) _loopTween.Kill();
    }
}
