using UnityEngine;
using Unity.Netcode;
using DG.Tweening;
using System;

[RequireComponent(typeof(NetworkObject))]
public class SpikeTrap : NetworkBehaviour, ITrapCycle
{
    [Header("Distance / Space")]
    [SerializeField, Min(0f)] private float raiseDistance = 1.2f;
    [SerializeField] private bool useLocalSpace = true;

    [Header("Timing")]
    [SerializeField, Min(0f)] private float raiseDuration = 0.35f;
    [SerializeField, Min(0f)] private float holdUpDuration = 0.8f;
    [SerializeField, Min(0f)] private float lowerDuration = 0.3f;
    [SerializeField, Min(0f)] private float holdDownDuration = 0.6f;

    [Header("Tween")]
    [SerializeField] private Ease easeUp = Ease.OutCubic;
    [SerializeField] private Ease easeDown = Ease.InCubic;

    [Header("Damage (Optional)")]
    [Tooltip("ถ้ามี ใส่ Trigger Collider ไว้เพื่อเปิด/ปิดช่วงทำดาเมจ")]
    [SerializeField] private Collider damageTrigger;

    [Header("Debug")]
    [SerializeField] private bool showGizmos = true;

    // runtime
    Vector3 _downLocal, _downWorld, _upTarget;
    Sequence _seq;

    // events (optional)
    public event Action OnSpikeUpStarted;
    public event Action OnSpikeUpHeld;
    public event Action OnSpikeDownStarted;
    public event Action OnSpikeDownHeld;

    void Awake()
    {
        _downLocal = transform.localPosition;
        _downWorld = transform.position;
        RecalcUpTarget();
        SetDamage(false);
    }

    void OnDisable() { _seq?.Kill(); }
    void OnDestroy() { _seq?.Kill(); }

    void OnValidate()
    {
        if (Application.isPlaying) return;
        _downLocal = transform.localPosition;
        _downWorld = transform.position;
        RecalcUpTarget();
    }

    void RecalcUpTarget()
    {
        if (useLocalSpace)
            _upTarget = _downLocal + Vector3.up * raiseDistance;
        else
            _upTarget = _downWorld + Vector3.up * raiseDistance;
    }

    void SetToDownInstant()
    {
        if (useLocalSpace) transform.localPosition = _downLocal;
        else transform.position = _downWorld;
    }

    void SetDamage(bool on)
    {
        if (damageTrigger) damageTrigger.enabled = on;
    }

    // ====== ITrapCycle ======
    public float GetCycleDuration()
    {
        return raiseDuration + holdUpDuration + lowerDuration + holdDownDuration;
    }

    public void ApplyOverrides(float newRaiseDistance, bool newUseLocalSpace)
    {
        raiseDistance = Mathf.Max(0f, newRaiseDistance);
        useLocalSpace = newUseLocalSpace;

        // อัปเดตฐานตำแหน่งใหม่ทันทีเผื่อโดนย้ายก่อนเริ่ม
        _downLocal = transform.localPosition;
        _downWorld = transform.position;
        RecalcUpTarget();
    }

    public void PlayOnceForAll(float startDelay = 0f)
    {
        if (!IsServer) return;

        // เล่นที่ Server เอง
        PlayLocal(startDelay);

        // บอก Client ให้เล่นด้วยเวลาเดียวกัน
        PlayOnceClientRpc(startDelay, raiseDuration, holdUpDuration, lowerDuration, holdDownDuration, raiseDistance, useLocalSpace);
    }

    [ClientRpc]
    void PlayOnceClientRpc(float startDelay, float ru, float hu, float ld, float hd, float dist, bool localSpace)
    {
        // sync ค่าที่จำเป็น
        raiseDuration = ru; holdUpDuration = hu; lowerDuration = ld; holdDownDuration = hd;
        ApplyOverrides(dist, localSpace);
        PlayLocal(startDelay);
    }

    // ====== ภายใน: เล่นลูป 1 รอบบนเครื่องปัจจุบัน ======
    void PlayLocal(float startDelay = 0f)
    {
        _seq?.Kill();

        // เผื่อโดนย้าย: ยึดฐานใหม่และเป้าใหม่ทุกครั้งก่อนเล่น
        _downLocal = transform.localPosition;
        _downWorld = transform.position;
        RecalcUpTarget();
        SetToDownInstant();
        SetDamage(false);

        _seq = DOTween.Sequence()
            .SetId(gameObject)
            .SetTarget(transform);

        if (startDelay > 0f) _seq.AppendInterval(startDelay);

        // Up
        _seq.AppendCallback(() =>
        {
            OnSpikeUpStarted?.Invoke();
            SetDamage(true);   // เปิดทำดาเมจตอนขึ้น/ค้าง
        });

        if (useLocalSpace)
            _seq.Append(transform.DOLocalMove(_upTarget, raiseDuration).SetEase(easeUp));
        else
            _seq.Append(transform.DOMove(_upTarget, raiseDuration).SetEase(easeUp));

        _seq.AppendCallback(() => OnSpikeUpHeld?.Invoke());
        _seq.AppendInterval(holdUpDuration);

        // Down
        _seq.AppendCallback(() =>
        {
            OnSpikeDownStarted?.Invoke();
            SetDamage(false);  // ปิดก่อนลง
        });

        if (useLocalSpace)
            _seq.Append(transform.DOLocalMove(_downLocal, lowerDuration).SetEase(easeDown));
        else
            _seq.Append(transform.DOMove(_downWorld, lowerDuration).SetEase(easeDown));

        _seq.AppendCallback(() => OnSpikeDownHeld?.Invoke());
        _seq.AppendInterval(holdDownDuration);
    }

    void OnDrawGizmos()
    {
        if (!showGizmos) return;

        Vector3 down = useLocalSpace
            ? (Application.isPlaying ? _downLocal : transform.localPosition)
            : (Application.isPlaying ? _downWorld : transform.position);

        if (!Application.isPlaying)
            _upTarget = (useLocalSpace ? down : transform.position) + Vector3.up * raiseDistance;

        Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(down, 0.1f);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(_upTarget, 0.1f);
        Gizmos.color = Color.green; Gizmos.DrawLine(down, _upTarget);
    }
}
