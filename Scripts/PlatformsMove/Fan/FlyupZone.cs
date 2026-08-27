using System.Collections.Generic;

using Unity.Netcode;

using UnityEngine;
using UnityEngine.Audio;

using DG.Tweening; // <--- สำคัญ: ต้องเพิ่มบรรทัดนี้



/// <summary>

/// โซนที่สร้างแรงลมเพื่อดัน (Push) หรือดึง (Pull) Rigidbody ที่เข้ามาในพื้นที่

/// สถานะจะถูกซิงค์ผ่านเน็ตเวิร์คและแสดงผลด้วย Particle Effects และ UI/DOTween Animation

/// </summary>

[RequireComponent(typeof(NetworkObject))]

public class FlyupZone : NetworkBehaviour, IActivatable, IWindModeActivatable

{

    #region Inspector Fields & Enums

    private enum DetectionShape { Box, Sphere, Capsule, Mesh, Cylinder }



    [Header("State (initial on Server)")]

    [SerializeField] private bool defaultActive = true;

    [Tooltip("Activate(false) → ถ้าติ๊ก true จะเป็น Pull แทน Disabled")]

    [SerializeField] private bool offMeansPullDown = true;



    [Header("Player Interaction")]

    [Tooltip("ถ้าติ๊ก: ผู้เล่นจะสามารถกระโดดได้ (รีเซ็ต jump count) ขณะอยู่ในโซนลมนี้")]

    [SerializeField] private bool allowJumpingInWind = true;



    [Header("Effects")]

    [Tooltip("Particle System ที่จะเล่นเมื่ออยู่ในโหมด Push (ลมดันขึ้น)")]

    [SerializeField] private ParticleSystem pushParticles;

    [Tooltip("Particle System ที่จะเล่นเมื่ออยู่ในโหมด Pull (ลมดูดลง)")]

    [SerializeField] private ParticleSystem pullParticles;



    [Header("UI & DOTween Animation")]

    [Tooltip("UI/GameObject ที่จะแสดงเมื่ออยู่ในโหมด Push (เช่น ลูกศรชี้ขึ้น)")]

    [SerializeField] private GameObject pushUIRoot;

    [Tooltip("UI/GameObject ที่จะแสดงเมื่ออยู่ในโหมด Pull (เช่น ลูกศรชี้ลง)")]

    [SerializeField] private GameObject pullUIRoot;

    [Tooltip("ระยะที่ UI จะขยับขึ้น-ลงในแอนิเมชัน (เช่น 0.15f)")]

    [SerializeField] private float uiBounceDistance = 0.15f;

    [Tooltip("ความเร็วของแอนิเมชันขึ้น-ลง (เช่น 0.7f)")]

    [SerializeField] private float uiBounceDuration = 0.7f;



    [Header("Pivot Settings")]

    [SerializeField] private Transform pivot;



    [Header("Detection Settings")]

    [SerializeField] private Vector3 detectPosition = Vector3.zero;

    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;

    [SerializeField] private Mesh meshPreview;

    [SerializeField] private LayerMask detectLayers = ~0;



    [Header("Box Settings")] [SerializeField] private Vector3 boxSize = Vector3.one;

    [Header("Sphere Settings")] [SerializeField] private float sphereRadius = 0.5f;

    [Header("Capsule Settings")] [SerializeField] private float capsuleRadius = 0.5f;

    [SerializeField] private float capsuleHeight = 2f;

    [Header("Cylinder Settings")] [SerializeField] private float cylinderRadius = 0.5f;

    [SerializeField] private float cylinderHeight = 2f;



    [Header("Push (Updraft) Settings")]

    [SerializeField] private Vector3 pushDirection = Vector3.up;

    [SerializeField] private float pushForce = 15f;

    [SerializeField] private AnimationCurve pushForceCurve = AnimationCurve.Linear(0, 1, 1, 1);

    [SerializeField] private bool pushUseGravityCounter = true;

    [SerializeField] private float pushGravityMultiplier = 1f;



    [Header("Pull (Downdraft) Settings")]

    [SerializeField] private Vector3 pullDirection = Vector3.down;

    [SerializeField] private float pullForce = 30f;

    [SerializeField] private AnimationCurve pullForceCurve = AnimationCurve.Linear(0, 1, 1, 1);

    [SerializeField] private bool pullUseGravityAssist = true;

    [SerializeField] private float pullGravityMultiplier = 2f;



    [Header("Smooth / Speed Limiter (ทั้ง Push/Pull)")]

    [SerializeField] private bool enableSmoothFloat = true;

    [SerializeField] private float maxAlongWindSpeed = 10f;

    [SerializeField] private float alongAcceleration = 4f;

    [SerializeField] private float alongDeceleration = 1.5f;

    [SerializeField] private float airDrag = 0.1f;



    [Header("Suction (ดูดเข้าศูนย์โซน)")]

    [SerializeField] private bool enableSuction = true;

    [SerializeField] private float suctionStrength = 10f;

    [SerializeField] private float suctionMaxRadius = 2.0f;

    [Tooltip("ถ้าติ๊ก: ดูดเข้าศูนย์เฉพาะระนาบตั้งฉากกับทิศลม (ไม่ไปสู้กับดึงลง/ดันขึ้น)")]

    [SerializeField] private bool suctionProjectOnPlane = true;



    [Header("Wind Effects (ทั้งสองโหมด)")]

    [SerializeField] private bool enableWindTurbulence = false;

    [SerializeField] private float turbulenceStrength = 2f;

    [SerializeField] private float turbulenceFrequency = 1f;

    [SerializeField] private bool enableWindGradient = false;

    [SerializeField] private float maxWindDistance = 5f;



    [Header("Platform Movement")]

    [SerializeField] private bool enablePlatformMovement = true;



    [Header("Gizmos")]

    [SerializeField] private Color gizmoColor = new Color(0f, 1f, 0f, 0.25f);

    [SerializeField] private bool showWindDirection = true;

    [SerializeField] private float windArrowLength = 2f;

    [Header("Audio")]

    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]

    [SerializeField] private AudioSource audioSource;

    [Tooltip("Audio Clip ที่จะเล่นเมื่อลมเริ่มทำงาน")]

    [SerializeField] private AudioClip windSound;

    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]

    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Tooltip("ระยะเวลาที่ใช้ในการ fade out เสียงเมื่อเปลี่ยนโหมด (วินาที)")]

    [SerializeField, Min(0f)] private float soundFadeOutDuration = 0.5f;

    #endregion



    #region Public Properties

    public bool AllowJumpingInWind => allowJumpingInWind;

    #endregion



    #region Runtime State

    private readonly NetworkVariable<bool> _zoneActiveNV = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private WindMode? _overrideMode;

    private readonly List<Rigidbody> _trackedRigidbodies = new();

    private readonly Dictionary<Rigidbody, float> _rigidbodyDistances = new();

    private readonly Dictionary<Rigidbody, float> _originalDrags = new();

    private Vector3 _lastPosition;

   

    // ----- DOTween Runtime State -----

    private Sequence _pushTweenSequence;

    private Sequence _pullTweenSequence;

    private Tween _audioFadeTween;

    // ---------------------------------

    #endregion



    #region Unity Lifecycle

    private void Awake()

    {

        if (!pivot) pivot = transform;

        if (pushDirection == Vector3.zero) pushDirection = Vector3.up;

        if (pullDirection == Vector3.zero) pullDirection = Vector3.down;

        pushDirection = pushDirection.normalized;

        pullDirection = pullDirection.normalized;

        InitializeAudio();

    }

    private void InitializeAudio()

    {

        if (audioSource != null)

        {

            audioSource.playOnAwake = false;

            audioSource.loop = true; // เสียงลมควรเป็น loop

            if (outputMixerGroup != null)

            {

                audioSource.outputAudioMixerGroup = outputMixerGroup;

            }

        }

    }



    public override void OnNetworkSpawn()

    {

        base.OnNetworkSpawn();

        if (IsServer)

        {

            _zoneActiveNV.Value = defaultActive;

        }

        ApplyActiveLocal(CurrentMode());

        _lastPosition = transform.position;

    }



    private void OnEnable() { _zoneActiveNV.OnValueChanged += OnZoneActiveChanged; }

   

    private void OnDisable()

    {

        _zoneActiveNV.OnValueChanged -= OnZoneActiveChanged;

       

        // หยุดแอนิเมชันเมื่อถูกปิด

        _pushTweenSequence?.Kill();

        _pullTweenSequence?.Kill();

        _audioFadeTween?.Kill();

        // หยุดเสียงเมื่อถูกปิด

        if (audioSource != null && audioSource.isPlaying)

        {

            audioSource.Stop();

        }

    }



    private void FixedUpdate()

    {

        HandlePlatformMovement();

        WindMode mode = CurrentMode();

        if (mode != WindMode.Disabled)

        {

            DetectAndTrackRigidbodies();

            ApplyWindForce(mode);

        }

        _lastPosition = transform.position;

    }

    #endregion



    #region Public Interface Implementation

    public void Activate(bool on)

    {

        _overrideMode = null;

        if (IsServer) _zoneActiveNV.Value = on;

        else RequestSetActiveServerRpc(on);

    }



    public void SetWindMode(WindMode mode)

    {

        _overrideMode = mode;

        ApplyActiveLocal(CurrentMode());

    }

    #endregion



    #region Network Logic

    [ServerRpc(RequireOwnership = false)]

    private void RequestSetActiveServerRpc(bool on) => _zoneActiveNV.Value = on;

    private void OnZoneActiveChanged(bool prev, bool next) => ApplyActiveLocal(CurrentMode());

    #endregion



    #region Core Logic

    private WindMode CurrentMode()

    {

        if (_overrideMode.HasValue) return _overrideMode.Value;

        if (_zoneActiveNV.Value) return WindMode.Push;

        return offMeansPullDown ? WindMode.Pull : WindMode.Disabled;

    }



    private void ApplyActiveLocal(WindMode mode)

    {

        UpdateParticleEffects(mode);

        UpdateUI(mode); // <--- เรียกเมธอด DOTween

        UpdateWindSound(mode); // <--- เรียกเมธอดเสียง

       

        if (mode == WindMode.Disabled)

        {

            for (int i = _trackedRigidbodies.Count - 1; i >= 0; i--)

            {

                Rigidbody rb = _trackedRigidbodies[i];

                if (rb && _originalDrags.TryGetValue(rb, out float drag))

                {

                    rb.linearDamping = drag;

                }

                NotifyWindExit(rb, this);

            }

            _trackedRigidbodies.Clear();

            _rigidbodyDistances.Clear();

            _originalDrags.Clear();

        }

    }



    private void UpdateParticleEffects(WindMode mode)
    {
        // --- ส่วนควบคุม Push Particles ---
        if (pushParticles != null)
        {
            bool shouldPlayPush = (mode == WindMode.Push);
            if (shouldPlayPush)
            {
                // ถ้าโหมดคือ Push แต่ยังไม่เล่น ก็สั่งเล่น
                if (!pushParticles.isPlaying)
                {
                    // หยุด particle อื่นก่อนเล่นใหม่
                    if (pushParticles.isStopped) pushParticles.Clear();
                    pushParticles.Play();
                }
            }
            else // ถ้าโหมดไม่ใช่ Push (คือ Pull หรือ Disabled)
            {
                // ถ้ามันเล่นอยู่ ก็สั่งหยุด
                if (pushParticles.isPlaying)
                {
                    pushParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }

        // --- ส่วนควบคุม Pull Particles ---
        if (pullParticles != null)
        {
            bool shouldPlayPull = (mode == WindMode.Pull);
            if (shouldPlayPull)
            {
                // ถ้าโหมดคือ Pull แต่ยังไม่เล่น ก็สั่งเล่น
                if (!pullParticles.isPlaying)
                {
                    // หยุด particle อื่นก่อนเล่นใหม่
                    if (pullParticles.isStopped) pullParticles.Clear();
                    pullParticles.Play();
                }
            }
            else // ถ้าโหมดไม่ใช่ Pull (คือ Push หรือ Disabled)
            {
                // ถ้ามันเล่นอยู่ ก็สั่งหยุด
                if (pullParticles.isPlaying)
                {
                    pullParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }
    }

   

    // --------------------------------------------------

    // เมธอดสำหรับควบคุม UI ด้วย DOTween

    private void UpdateUI(WindMode mode)

    {

        // 1. หยุดและรีเซ็ตแอนิเมชันเดิมทั้งหมด

        _pushTweenSequence?.Kill(true); // Kill(true) เพื่อรีเซ็ตตำแหน่งด้วย

        _pullTweenSequence?.Kill(true);

        if (pushUIRoot != null) pushUIRoot.SetActive(false);

        if (pullUIRoot != null) pullUIRoot.SetActive(false);



        // 2. เริ่มแอนิเมชันสำหรับโหมดที่ใช้งาน

        if (mode == WindMode.Push && pushUIRoot != null)

        {

            pushUIRoot.SetActive(true);

            _pushTweenSequence = CreateBounceSequence(pushUIRoot.transform);

            _pushTweenSequence.Play();

        }

        else if (mode == WindMode.Pull && pullUIRoot != null)

        {

            pullUIRoot.SetActive(true);

            _pullTweenSequence = CreateBounceSequence(pullUIRoot.transform);

            _pullTweenSequence.Play();

        }

    }



    private Sequence CreateBounceSequence(Transform targetTransform)

    {

        Vector3 originalLocalPos = targetTransform.localPosition;

       

        Sequence sequence = DOTween.Sequence();

       

        // Bounce: ขึ้น > ลง > กลับเข้าที่เดิม (ทำซ้ำ)

        sequence.Append(targetTransform.DOLocalMoveY(originalLocalPos.y + uiBounceDistance, uiBounceDuration * 0.35f).SetEase(Ease.OutSine));

        sequence.Append(targetTransform.DOLocalMoveY(originalLocalPos.y - uiBounceDistance * 0.5f, uiBounceDuration * 0.35f).SetEase(Ease.InSine));

        sequence.Append(targetTransform.DOLocalMoveY(originalLocalPos.y, uiBounceDuration * 0.3f).SetEase(Ease.OutQuad));

       

        // ทำให้เล่นซ้ำไปเรื่อยๆ และใช้ FixedUpdate เพื่อความสม่ำเสมอ

        sequence.SetLoops(-1, LoopType.Restart).SetUpdate(UpdateType.Fixed);



        return sequence;

    }

    // --------------------------------------------------

    private void UpdateWindSound(WindMode mode)

    {

        if (audioSource == null || windSound == null) return;

        // หยุด fade tween เดิมถ้ามี

        _audioFadeTween?.Kill();

        // ถ้าโหมดไม่ใช่ Disabled ให้เล่นเสียง

        if (mode != WindMode.Disabled)

        {

            // ถ้ายังไม่เล่น ให้เริ่มเล่น

            if (!audioSource.isPlaying)

            {

                audioSource.clip = windSound;

                audioSource.volume = 1f; // ตั้งค่า volume เป็น 1 ก่อนเล่น

                audioSource.Play();

            }

            else

            {

                // ถ้ากำลังเล่นอยู่แล้ว ให้ fade in (ถ้า volume ไม่ใช่ 1)

                if (audioSource.volume < 1f)

                {

                    _audioFadeTween = audioSource.DOFade(1f, soundFadeOutDuration);

                }

            }

        }

        else

        {

            // ถ้าโหมดเป็น Disabled ให้ fade out

            if (audioSource.isPlaying)

            {

                _audioFadeTween = audioSource.DOFade(0f, soundFadeOutDuration)

                    .OnComplete(() =>

                    {

                        if (audioSource != null)

                        {

                            audioSource.Stop();

                            audioSource.volume = 1f; // รีเซ็ต volume สำหรับครั้งถัดไป

                        }

                    });

            }

        }

    }

    // --------------------------------------------------



    private void DetectAndTrackRigidbodies()

    {

        Collider[] detected = GetOverlaps();

        for (int i = _trackedRigidbodies.Count - 1; i >= 0; i--)

        {

            Rigidbody rb = _trackedRigidbodies[i];

            bool stillInside = false;

            if (rb != null && detected != null)

            {

                foreach (Collider c in detected)

                {

                    if (c && c.attachedRigidbody == rb)

                    {

                        stillInside = true;

                        break;

                    }

                }

            }

            if (!stillInside)

            {

                if (rb && _originalDrags.TryGetValue(rb, out float drag)) rb.linearDamping = drag;

                _originalDrags.Remove(rb);

                _rigidbodyDistances.Remove(rb);

                _trackedRigidbodies.RemoveAt(i);

                NotifyWindExit(rb, this);

            }

        }



        if (detected == null) return;

        Vector3 zoneCenter = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);



        foreach (Collider c in detected)

        {

            Rigidbody rb = c ? c.attachedRigidbody : null;

            if (!rb) continue;

            if (!_trackedRigidbodies.Contains(rb))

            {

                _trackedRigidbodies.Add(rb);

                if (!_originalDrags.ContainsKey(rb)) _originalDrags[rb] = rb.linearDamping;

                NotifyWindEnter(rb, this);

            }

            _rigidbodyDistances[rb] = Vector3.Distance(rb.worldCenterOfMass, zoneCenter);

        }

    }



    private Collider[] GetOverlaps()

    {

        Vector3 worldPos = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);

        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;

        Vector3 up = pivot ? pivot.up : Vector3.up;



        switch (detectionShape)

        {

            case DetectionShape.Box: return Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers);

            case DetectionShape.Sphere: return Physics.OverlapSphere(worldPos, sphereRadius, detectLayers);

            case DetectionShape.Capsule:

                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);

                Vector3 a = worldPos + up * hh;

                Vector3 b = worldPos - up * hh;

                return Physics.OverlapCapsule(a, b, capsuleRadius, detectLayers);

            case DetectionShape.Cylinder:

                float half = cylinderHeight * 0.5f;

                Vector3 top = worldPos + up * half;

                Vector3 bottom = worldPos - up * half;

                return Physics.OverlapCapsule(top, bottom, cylinderRadius, detectLayers);

            default: return null;

        }

    }



    private void ApplyWindForce(WindMode mode)

    {

        bool isPush = (mode == WindMode.Push);

        Vector3 localDir = isPush ? pushDirection : pullDirection;

        float baseForce = isPush ? pushForce : pullForce;

        AnimationCurve curve = isPush ? pushForceCurve : pullForceCurve;

        Vector3 worldWind = (pivot ? pivot.rotation : transform.rotation) * localDir;

        Vector3 zoneCenter = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);



        foreach (Rigidbody rb in _trackedRigidbodies)

        {

            if (!rb) continue;



            Vector3 directionToTarget = rb.worldCenterOfMass - zoneCenter;

            float distanceToTarget = directionToTarget.magnitude;

            if (Physics.Raycast(zoneCenter, directionToTarget.normalized, out RaycastHit hit, distanceToTarget, detectLayers))

            {

                if (hit.rigidbody != rb) continue;

            }



            if (enableSmoothFloat) rb.linearDamping = airDrag;



            float currentForce = baseForce;

            if (enableWindGradient && _rigidbodyDistances.TryGetValue(rb, out float dist))

            {

                float falloff = 1f - Mathf.Clamp01(dist / Mathf.Max(0.0001f, maxWindDistance));

                currentForce *= falloff;

            }



            currentForce *= curve.Evaluate(Time.time % 1f);

            Vector3 finalAccel = worldWind * currentForce;



            if (enableSmoothFloat)

            {

                Vector3 velocity = rb.linearVelocity;

                Vector3 along = Vector3.Project(velocity, worldWind);

                float speed = along.magnitude;

                if (speed > maxAlongWindSpeed)

                {

                    Vector3 excess = along - (worldWind * maxAlongWindSpeed);

                    Vector3 decel = -excess * alongDeceleration;

                    rb.AddForce(decel, ForceMode.Acceleration);

                }

                else

                {

                    finalAccel = worldWind * (currentForce * alongAcceleration);

                }

            }



            if (enableWindTurbulence)

            {

                Vector3 turb = new(

                    Mathf.PerlinNoise(Time.time * turbulenceFrequency, 0f) - 0.5f,

                    Mathf.PerlinNoise(Time.time * turbulenceFrequency + 100f, 0f) - 0.5f,

                    Mathf.PerlinNoise(Time.time * turbulenceFrequency + 200f, 0f) - 0.5f);

                finalAccel += turb * turbulenceStrength;

            }



            if (isPush && pushUseGravityCounter) finalAccel += -Physics.gravity * pushGravityMultiplier;

            else if (!isPush && pullUseGravityAssist) finalAccel += Physics.gravity * pullGravityMultiplier;



            if (enableSuction)

            {

                Vector3 toCenter = zoneCenter - rb.worldCenterOfMass;

                float distToCenter = toCenter.magnitude;

                if (distToCenter > 0.001f)

                {

                    if (suctionProjectOnPlane) toCenter = Vector3.ProjectOnPlane(toCenter, worldWind.normalized);

                    float suctionWeight = Mathf.Clamp01(1f - (distToCenter / Mathf.Max(0.0001f, suctionMaxRadius)));

                    finalAccel += toCenter.normalized * (suctionStrength * suctionWeight);

                }

            }

            rb.AddForce(finalAccel, ForceMode.Acceleration);

        }

    }

    #endregion



    #region Helper Methods

    private void HandlePlatformMovement()

    {

        if (!enablePlatformMovement) return;

        Vector3 delta = transform.position - _lastPosition;

        if (delta != Vector3.zero)

        {

            foreach (Rigidbody rb in _trackedRigidbodies)

            {

                if (rb) rb.MovePosition(rb.position + delta);

            }

        }

    }



    private static void NotifyWindEnter(Rigidbody rb, FlyupZone sourceZone)

    {

        // (สมมติว่ามี WindContactRelay อยู่)

        // WindContactRelay relay = rb ? rb.GetComponent<WindContactRelay>() ?? rb.GetComponentInParent<WindContactRelay>() : null;

        // relay?.OnEnterZone(sourceZone);

    }



    private static void NotifyWindExit(Rigidbody rb, FlyupZone sourceZone)

    {

        // (สมมติว่ามี WindContactRelay อยู่)

        // WindContactRelay relay = rb ? rb.GetComponent<WindContactRelay>() ?? rb.GetComponentInParent<WindContactRelay>() : null;

        // relay?.OnExitZone(sourceZone);

    }

    #endregion



    #region Gizmos

    private void OnDrawGizmos()

    {

        if (!pivot) pivot = transform;

        Gizmos.color = gizmoColor;

        Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);

        switch (detectionShape)

        {

            case DetectionShape.Box: Gizmos.DrawCube(detectPosition, boxSize); Gizmos.DrawWireCube(detectPosition, boxSize); break;

            case DetectionShape.Sphere: Gizmos.DrawSphere(detectPosition, sphereRadius); Gizmos.DrawWireSphere(detectPosition, sphereRadius); break;

            case DetectionShape.Capsule:

                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);

                Vector3 upLocal = Vector3.up * hh;

                Gizmos.DrawWireSphere(detectPosition + upLocal, capsuleRadius);

                Gizmos.DrawWireSphere(detectPosition - upLocal, capsuleRadius);

                Gizmos.DrawLine(detectPosition + upLocal + Vector3.forward * capsuleRadius, detectPosition - upLocal + Vector3.forward * capsuleRadius);

                Gizmos.DrawLine(detectPosition + upLocal - Vector3.forward * capsuleRadius, detectPosition - upLocal - Vector3.forward * capsuleRadius);

                Gizmos.DrawLine(detectPosition + upLocal + Vector3.right * capsuleRadius, detectPosition - upLocal + Vector3.right * capsuleRadius);

                Gizmos.DrawLine(detectPosition + upLocal - Vector3.right * capsuleRadius, detectPosition - upLocal - Vector3.right * capsuleRadius);

                break;

            case DetectionShape.Cylinder: DrawWireCylinder(detectPosition, cylinderRadius, cylinderHeight, 32); break;

            case DetectionShape.Mesh:

                if (meshPreview)

                {

                    Gizmos.DrawMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);

                    Gizmos.color = new Color(0f, 0f, 0f, gizmoColor.a + 0.1f);

                    Gizmos.DrawWireMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);

                }

                break;

        }



        if (showWindDirection)

        {

            Gizmos.matrix = Matrix4x4.identity;

            Vector3 s = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);

            Gizmos.color = Color.cyan;

            Vector3 pushWorld = (pivot ? pivot.rotation : transform.rotation) * pushDirection.normalized;

            Gizmos.DrawLine(s, s + pushWorld * windArrowLength);

            Gizmos.color = Color.magenta;

            Vector3 pullWorld = (pivot ? pivot.rotation : transform.rotation) * pullDirection.normalized;

            Gizmos.DrawLine(s, s + pullWorld * windArrowLength * 0.8f);

        }

    }



    private static void DrawWireCylinder(Vector3 centerLocal, float radius, float height, int ringSegments = 24)

    {

        float half = height * 0.5f;

        Vector3 top = centerLocal + Vector3.up * half;

        Vector3 bottom = centerLocal - Vector3.up * half;

        DrawWireCircle(top, radius, ringSegments);

        DrawWireCircle(bottom, radius, ringSegments);

        int uprights = 8;

        float step = Mathf.PI * 2f / uprights;

        for (int i = 0; i < uprights; i++)

        {

            float a = i * step;

            Vector3 rim = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);

            Gizmos.DrawLine(bottom + rim, top + rim);

        }

    }



    private static void DrawWireCircle(Vector3 centerLocal, float radius, int segments = 24)

    {

        Vector3 prev = centerLocal + new Vector3(radius, 0f, 0f);

        float step = Mathf.PI * 2f / Mathf.Max(8, segments);

        for (int i = 1; i <= segments; i++)

        {

            float a = i * step;

            Vector3 p = centerLocal + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);

            Gizmos.DrawLine(prev, p);

            prev = p;

        }

    }

    #endregion

}