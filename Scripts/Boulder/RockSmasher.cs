using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using DG.Tweening;
using System;
using UnityEngine.Audio;

[RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class RockSmasher : NetworkBehaviour, IFreezeListener
{
    public enum Axis { X, Y, Z }

    [Header("Motion")]
    [SerializeField] private Axis moveAxis = Axis.Y;
    [SerializeField] private bool useLocalSpace = true;
    [SerializeField, Min(0f)] private float downDistance = 3f;
    [SerializeField, Min(0.05f)] private float downDuration = 0.35f;
    [SerializeField] private Ease downEase = Ease.InQuad;
    [SerializeField, Min(0f)] private float pauseAtBottom = 0.05f;
    [SerializeField, Min(0.05f)] private float upDuration = 0.45f;
    [SerializeField] private Ease upEase = Ease.OutQuad;
    [SerializeField] private bool autoLoop = true;
    [SerializeField] private float loopCooldown = 0.25f;
    [Tooltip("Delay ก่อนเริ่มทำงาน (วินาที) ใช้สำหรับ stagger แต่ละตัวไม่ให้ทำงานพร้อมกัน")]
    [SerializeField, Min(0f)] private float startDelay = 0f;

    [Header("Slide zone death (optional)")]
    [Tooltip("ถ้าเปิดและลาก SplineSlideZone ของด่านสไลด์ — ผู้เล่นที่กำลังสไลด์อยู่จะตายแบบ SlideZone (checkpoint/เพื่อน) แทน Kill() ทั่วไป")]
    [SerializeField] private bool useSlideZoneObstacleDeath = false;
    [SerializeField] private SplineSlideZone slideSplineZone;
    
    [Header("Detection")]
    [Tooltip("ลาก GameObject ที่มี Trigger Collider มาใส่เพื่อกำหนดพื้นที่ตรวจจับ")]
    [SerializeField] private GameObject detectZone;
    [SerializeField] private LayerMask detectMask = ~0;
    [SerializeField] private string[] destroyTags = { "Boulder" };

    [Header("Gizmos")]
    [SerializeField] private bool showGizmos = true;
    [SerializeField] private Color gizmoColor = new(1f, 0.3f, 0.2f, 0.25f);
    [SerializeField] private bool showMoveRange = true;
    [SerializeField] private Color moveRangeColor = Color.yellow;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงตอนถึงก้น/กระแทก (ended)")]
    [SerializeField] private AudioClip endedSound;
    [Tooltip("เสียงตอนกลับขึ้นถึงจุดเริ่มต้น (return)")]
    [SerializeField] private AudioClip returnSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    // --- internals ---
    private Vector3 _startLocal, _startWorld;
    private Vector3 _axisDir;
    private Vector3 _bottomLocalOrWorld;

    private Tween _tween;
    private Coroutine _loopCo;

    // Freeze
    private bool _frozen = false;

    private NetworkTransform _nt;

    void Awake()
    {
        _startLocal = transform.localPosition;
        _startWorld = transform.position;
        _axisDir = moveAxis == Axis.X ? Vector3.right : (moveAxis == Axis.Y ? Vector3.up : Vector3.forward);
        _nt = GetComponent<NetworkTransform>();
        InitializeAudio();
    }

    private void InitializeAudio()
    {
        if (audioSource == null) return;
        audioSource.playOnAwake = false;
        if (outputMixerGroup != null)
        {
            audioSource.outputAudioMixerGroup = outputMixerGroup;
        }
    }

    public override void OnNetworkSpawn()
    {
        _bottomLocalOrWorld = useLocalSpace
            ? _startLocal - _axisDir * downDistance
            : _startWorld - _axisDir * downDistance;

        if (autoLoop && IsServer)
        {
            _loopCo = StartCoroutine(SmashLoopCo());
        }
    }

    // ====================== Continuous detect while frozen ======================
    void Update()
    {
        // ขณะที่ถูก Freeze → ตรวจจับ kill zone ทุกเฟรม ไม่ว่าอยู่ตำแหน่งไหน
        if (_frozen && IsServer)
        {
            DoSmashDetectAndDestroy();
        }
    }

    // ====================== DOTween Loop ======================
    private IEnumerator SmashLoopCo()
    {
        // startDelay stagger
        if (startDelay > 0f)
            yield return new WaitForSeconds(startDelay);

        while (true)
        {
            // --- รอถ้าถูก Freeze ---
            while (_frozen) yield return null;

            // --- ขยับลง ---
            if (useLocalSpace)
                _tween = transform.DOLocalMove(_bottomLocalOrWorld, downDuration).SetEase(downEase);
            else
                _tween = transform.DOMove(_bottomLocalOrWorld, downDuration).SetEase(downEase);

            // รอ tween เสร็จ (หยุดชั่วคราวถ้าถูก freeze)
            while (_tween != null && _tween.IsActive() && !_tween.IsComplete())
            {
                if (_frozen && _tween.IsActive())
                    _tween.Pause();
                else if (!_frozen && _tween.IsActive() && !_tween.IsPlaying())
                    _tween.Play();
                yield return null;
            }

            // --- พักที่ก้น + ตรวจจับทำลาย ---
            if (IsServer)
                DoSmashDetectAndDestroy();

            if (IsServer) PlayEndedSoundClientRpc();

            if (pauseAtBottom > 0f)
                yield return new WaitForSeconds(pauseAtBottom);

            // --- รอถ้าถูก Freeze ---
            while (_frozen) yield return null;

            // --- ขยับขึ้น ---
            if (useLocalSpace)
                _tween = transform.DOLocalMove(_startLocal, upDuration).SetEase(upEase);
            else
                _tween = transform.DOMove(_startWorld, upDuration).SetEase(upEase);

            // รอ tween เสร็จ (หยุดชั่วคราวถ้าถูก freeze)
            while (_tween != null && _tween.IsActive() && !_tween.IsComplete())
            {
                if (_frozen && _tween.IsActive())
                    _tween.Pause();
                else if (!_frozen && _tween.IsActive() && !_tween.IsPlaying())
                    _tween.Play();
                yield return null;
            }

            if (IsServer) PlayReturnSoundClientRpc();

            // --- cooldown ---
            if (loopCooldown > 0f)
                yield return new WaitForSeconds(loopCooldown);
        }
    }

    [ClientRpc]
    private void PlayEndedSoundClientRpc()
    {
        if (audioSource != null && endedSound != null)
        {
            audioSource.PlayOneShot(endedSound);
        }
        else if (endedSound != null)
        {
            AudioSource.PlayClipAtPoint(endedSound, transform.position);
        }
    }

    [ClientRpc]
    private void PlayReturnSoundClientRpc()
    {
        if (audioSource != null && returnSound != null)
        {
            audioSource.PlayOneShot(returnSound);
        }
        else if (returnSound != null)
        {
            AudioSource.PlayClipAtPoint(returnSound, transform.position);
        }
    }

    // ====================== Freeze callbacks ======================
    public void OnFreezeChanged(bool on)
    {
        if (on)
        {
            if (_frozen) return;
            _frozen = true;

            // Pause tween ทันที
            if (_tween != null && _tween.IsActive() && _tween.IsPlaying())
                _tween.Pause();
        }
        else
        {
            if (!_frozen) return;
            _frozen = false;

            // Resume tween
            if (_tween != null && _tween.IsActive() && !_tween.IsPlaying())
                _tween.Play();
        }
    }

    // ====================== Cleanup ======================
    public override void OnNetworkDespawn()
    {
        _tween?.Kill();
        if (_loopCo != null)
        {
            StopCoroutine(_loopCo);
            _loopCo = null;
        }
    }

    private void OnDisable()
    {
        _tween?.Kill();
        if (_loopCo != null)
        {
            StopCoroutine(_loopCo);
            _loopCo = null;
        }
    }

    // ====================== Impact & Destroy ======================
    private void DoSmashDetectAndDestroy()
    {
        if (detectZone == null) return;

        // ดึง trigger collider ทุกตัวใน detectZone (รวม children)
        Collider[] zoneCols = detectZone.GetComponentsInChildren<Collider>();
        if (zoneCols == null || zoneCols.Length == 0) return;

        HashSet<Collider> zoneColSet = new HashSet<Collider>();
        HashSet<Collider> hitSet = new HashSet<Collider>();

        foreach (var zoneCol in zoneCols)
        {
            if (!zoneCol.isTrigger) continue;
            zoneColSet.Add(zoneCol);

            Collider[] hits = OverlapForCollider(zoneCol);
            if (hits != null)
            {
                foreach (var h in hits)
                    hitSet.Add(h);
            }
        }

        if (hitSet.Count == 0) return;

        foreach (Collider c in hitSet)
        {
            if (c == null || zoneColSet.Contains(c)) continue; // ข้ามตัว detect zone เอง

            // เช็กว่าเป็น Player หรือไม่ → Kill
            if (c.CompareTag("Player"))
            {
                var playerDeath = c.GetComponentInParent<PlayerDeath>();
                if (playerDeath != null)
                {
                    // ★ ข้ามผู้เล่นที่มี immunity หรือตายไปแล้ว — กัน Kill/DeadBody ซ้ำ
                    if (playerDeath.IsRespawnImmune) { continue; }
                    if (playerDeath.IsHiddenState) { continue; }
                    
                    if (useSlideZoneObstacleDeath && slideSplineZone != null && IsServer
                        && playerDeath.TryGetComponent<NetworkObject>(out var playerNo) && playerNo.IsSpawned
                        && slideSplineZone.TryServerHandleSlideObstacleDeath(playerNo.OwnerClientId))
                    {
                        // handled by slide zone
                    }
                    else
                    {
                        playerDeath.Kill();
                    }
                }
                continue;
            }

            // เคสพิเศษ Boulder ที่มีสคริปต์ของคุณ
            var boulder = c.GetComponentInParent<RollingBoulder>();
            if (boulder != null)
            {
                boulder.KillImmediateServer(); // เรียกฝั่งเซิร์ฟ
                continue;
            }

            // เช็ก Tag ตาม destroyTags
            bool tagMatch = MatchesDestroyTags(c);
            if (!tagMatch) continue;

            var no = c.GetComponentInParent<NetworkObject>();
            if (no != null && no.IsSpawned)
            {
                no.Despawn(true); // true = Destroy object
            }
            else
            {
                var go = c.attachedRigidbody ? c.attachedRigidbody.gameObject : c.gameObject;
                Destroy(go);
            }
        }
    }

    /// <summary>
    /// Overlap physics query สำหรับ collider แต่ละประเภท (Box, Sphere, Capsule, fallback bounds)
    /// </summary>
    private Collider[] OverlapForCollider(Collider zoneCol)
    {
        if (zoneCol is BoxCollider box)
        {
            Vector3 worldCenter = box.transform.TransformPoint(box.center);
            Vector3 halfExtents = Vector3.Scale(box.size * 0.5f, box.transform.lossyScale);
            halfExtents = new Vector3(Mathf.Abs(halfExtents.x), Mathf.Abs(halfExtents.y), Mathf.Abs(halfExtents.z));
            return Physics.OverlapBox(worldCenter, halfExtents, box.transform.rotation, detectMask, QueryTriggerInteraction.Collide);
        }
        else if (zoneCol is SphereCollider sphere)
        {
            Vector3 worldCenter2 = sphere.transform.TransformPoint(sphere.center);
            float maxScale = Mathf.Max(Mathf.Abs(sphere.transform.lossyScale.x),
                                       Mathf.Abs(sphere.transform.lossyScale.y),
                                       Mathf.Abs(sphere.transform.lossyScale.z));
            float worldRadius = sphere.radius * maxScale;
            return Physics.OverlapSphere(worldCenter2, worldRadius, detectMask, QueryTriggerInteraction.Collide);
        }
        else if (zoneCol is CapsuleCollider capsule)
        {
            Vector3 worldCenter3 = capsule.transform.TransformPoint(capsule.center);
            float scale = Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x),
                                    Mathf.Abs(capsule.transform.lossyScale.z));
            float worldRadius2 = capsule.radius * scale;
            float worldHeight = capsule.height * Mathf.Abs(capsule.transform.lossyScale[capsule.direction]);
            float halfH = Mathf.Max(worldHeight * 0.5f - worldRadius2, 0f);

            Vector3 dir = capsule.direction == 0 ? capsule.transform.right
                        : capsule.direction == 1 ? capsule.transform.up
                        : capsule.transform.forward;
            Vector3 a = worldCenter3 - dir * halfH;
            Vector3 b = worldCenter3 + dir * halfH;
            return Physics.OverlapCapsule(a, b, worldRadius2, detectMask, QueryTriggerInteraction.Collide);
        }
        else
        {
            // fallback: ใช้ bounds
            return Physics.OverlapBox(zoneCol.bounds.center, zoneCol.bounds.extents, Quaternion.identity, detectMask, QueryTriggerInteraction.Collide);
        }
    }

    private bool MatchesDestroyTags(Collider col)
    {
        if (destroyTags == null || destroyTags.Length == 0) return false;
        for (int i = 0; i < destroyTags.Length; i++)
        {
            if (col.CompareTag(destroyTags[i])) return true;
        }
        return false;
    }

    // ====================== Gizmos ======================
    void OnValidate()
    {
        if (Application.isPlaying) return;
        _startLocal = transform.localPosition;
        _startWorld = transform.position;
        _axisDir = moveAxis == Axis.X ? Vector3.right : (moveAxis == Axis.Y ? Vector3.up : Vector3.forward);
        _bottomLocalOrWorld = useLocalSpace
            ? _startLocal - _axisDir * downDistance
            : _startWorld - _axisDir * downDistance;
    }

    void OnDrawGizmos()
    {
        if (!showMoveRange) return;

        Vector3 axisDir = moveAxis == Axis.X ? Vector3.right : (moveAxis == Axis.Y ? Vector3.up : Vector3.forward);

        // Gizmos วาดใน World Space เสมอ → ต้องแปลง local → world ก่อน
        Vector3 startWorld;
        if (useLocalSpace)
        {
            Vector3 localPos = Application.isPlaying ? _startLocal : transform.localPosition;
            startWorld = transform.parent != null
                ? transform.parent.TransformPoint(localPos)
                : localPos;
        }
        else
        {
            startWorld = Application.isPlaying ? _startWorld : transform.position;
        }

        Vector3 bottomWorld = startWorld - axisDir * downDistance;

        // เส้นแสดงระยะขยับ
        Gizmos.color = moveRangeColor;
        Gizmos.DrawLine(startWorld, bottomWorld);

        // จุดเริ่ม (สีฟ้า) / จุดสุด (สีแดง)
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(startWorld, 0.15f);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(bottomWorld, 0.15f);
    }

    void OnDrawGizmosSelected()
    {
        if (!showGizmos || detectZone == null) return;

        Collider[] zoneCols = detectZone.GetComponentsInChildren<Collider>();
        if (zoneCols == null || zoneCols.Length == 0) return;

        Gizmos.color = gizmoColor;

        foreach (var zoneCol in zoneCols)
        {
            if (!zoneCol.isTrigger) continue;

            if (zoneCol is BoxCollider box)
            {
                Vector3 worldCenter = box.transform.TransformPoint(box.center);
                Vector3 size = Vector3.Scale(box.size, box.transform.lossyScale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                Matrix4x4 m = Matrix4x4.TRS(worldCenter, box.transform.rotation, Vector3.one);
                Gizmos.matrix = m;
                Gizmos.DrawCube(Vector3.zero, size);
                Gizmos.DrawWireCube(Vector3.zero, size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else if (zoneCol is SphereCollider sphere)
            {
                Vector3 worldCenter2 = sphere.transform.TransformPoint(sphere.center);
                float maxScale = Mathf.Max(Mathf.Abs(sphere.transform.lossyScale.x),
                                           Mathf.Abs(sphere.transform.lossyScale.y),
                                           Mathf.Abs(sphere.transform.lossyScale.z));
                float worldRadius = sphere.radius * maxScale;
                Gizmos.DrawSphere(worldCenter2, worldRadius);
                Gizmos.DrawWireSphere(worldCenter2, worldRadius);
            }
            else if (zoneCol is CapsuleCollider capsule)
            {
                Vector3 worldCenter3 = capsule.transform.TransformPoint(capsule.center);
                float scale = Mathf.Max(Mathf.Abs(capsule.transform.lossyScale.x),
                                        Mathf.Abs(capsule.transform.lossyScale.z));
                float worldRadius2 = capsule.radius * scale;
                float worldHeight = capsule.height * Mathf.Abs(capsule.transform.lossyScale[capsule.direction]);
                float halfH = Mathf.Max(worldHeight * 0.5f - worldRadius2, 0f);

                Vector3 dir = capsule.direction == 0 ? capsule.transform.right
                            : capsule.direction == 1 ? capsule.transform.up
                            : capsule.transform.forward;
                Gizmos.DrawSphere(worldCenter3 + dir * halfH, worldRadius2);
                Gizmos.DrawSphere(worldCenter3 - dir * halfH, worldRadius2);
                Gizmos.DrawWireSphere(worldCenter3 + dir * halfH, worldRadius2);
                Gizmos.DrawWireSphere(worldCenter3 - dir * halfH, worldRadius2);
            }
            else
            {
                // fallback: ใช้ bounds
                Gizmos.DrawWireCube(zoneCol.bounds.center, zoneCol.bounds.size);
            }
        }
    }
}
