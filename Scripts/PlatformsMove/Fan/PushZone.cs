using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using DG.Tweening;

/// <summary>
/// โซนลมแนวนอนที่เป่าผู้เล่นให้ตกหน้าผา
/// แก้ปัญหาการพุ่งออกแนวเฉียงและกันไม่ให้ผู้เล่นเดินผ่านลม
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[AddComponentMenu("WindZone/Push Zone")]
public class PushZone : NetworkBehaviour, IActivatable, IWindModeActivatable
{
    #region Inspector Fields & Enums
    private enum DetectionShape { Box, Sphere, Capsule, Mesh, Cylinder }

    [Header("State (initial on Server)")]
    [SerializeField] private bool defaultActive = true;
    [Tooltip("Activate(false) → ถ้าติ๊ก true จะเป็น Pull แทน Disabled")]
    [SerializeField] private bool offMeansPull = false;

    [Header("Player Interaction")]
    [Tooltip("ถ้าติ๊ก: ผู้เล่นจะสามารถกระโดดได้ (รีเซ็ต jump count) ขณะอยู่ในโซนลมนี้")]
    [SerializeField] private bool allowJumpingInWind = false;

    [Header("Effects")]
    [Tooltip("Particle System ที่จะเล่นเมื่ออยู่ในโหมด Push (ลมเป่า)")]
    [SerializeField] private ParticleSystem pushParticles;
    [Tooltip("Particle System ที่จะเล่นเมื่ออยู่ในโหมด Pull (ลมดูด)")]
    [SerializeField] private ParticleSystem pullParticles;

    [Header("UI & DOTween Animation")]
    [Tooltip("UI/GameObject ที่จะแสดงเมื่ออยู่ในโหมด Push (เช่น ลูกศรชี้ไปข้างหน้า)")]
    [SerializeField] private GameObject pushUIRoot;
    [Tooltip("UI/GameObject ที่จะแสดงเมื่ออยู่ในโหมด Pull (เช่น ลูกศรชี้กลับ)")]
    [SerializeField] private GameObject pullUIRoot;
    [Tooltip("ระยะที่ UI จะขยับไปมาในแอนิเมชัน (เช่น 0.15f)")]
    [SerializeField] private float uiBounceDistance = 0.15f;
    [Tooltip("ความเร็วของแอนิเมชัน (เช่น 0.7f)")]
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

    [Header("Push (Forward) Settings")]
    [Tooltip("ทิศทางลมเป่า (ควรเป็นแนวนอน)")]
    [SerializeField] private Vector3 pushDirection = Vector3.forward;
    [Tooltip("แรงลมเป่า (แนวนอน)")]
    [SerializeField] private float pushForce = 20f;
    [SerializeField] private AnimationCurve pushForceCurve = AnimationCurve.Linear(0, 1, 1, 1);
    [Tooltip("จำกัดความเร็วสูงสุดในทิศลม")]
    [SerializeField] private float maxPushSpeed = 15f;

    [Header("Pull (Backward) Settings")]
    [Tooltip("ทิศทางลมดูด (ควรเป็นแนวนอน)")]
    [SerializeField] private Vector3 pullDirection = Vector3.back;
    [Tooltip("แรงลมดูด (แนวนอน)")]
    [SerializeField] private float pullForce = 20f;
    [SerializeField] private AnimationCurve pullForceCurve = AnimationCurve.Linear(0, 1, 1, 1);
    [Tooltip("จำกัดความเร็วสูงสุดในทิศลมดูด")]
    [SerializeField] private float maxPullSpeed = 15f;

    [Header("Movement Resistance - Against Wind (ต้านการเดินสวนลม)")]
    [Tooltip("เปิดใช้งานการต้านการเดินสวนลม")]
    [SerializeField] private bool enableResistanceAgainstWind = true;
    [Tooltip("แรงต้านการเคลื่อนที่ที่สวนทางกับทิศลม (ป้องกันการเดินสวนลม)")]
    [SerializeField] private float movementResistanceAgainst = 50f;
    [Tooltip("ความเร็วสูงสุดที่ผู้เล่นสามารถเคลื่อนที่สวนทิศลมได้")]
    [SerializeField] private float maxResistSpeedAgainst = 2f;

    [Header("Movement Resistance - Sideways (ต้านการเดินด้านข้างลม)")]
    [Tooltip("เปิดใช้งานการต้านการเดินด้านข้างลม")]
    [SerializeField] private bool enableResistanceSideways = true;
    [Tooltip("แรงต้านการเคลื่อนที่ด้านข้างลม (ป้องกันการเดินผ่านด้านข้าง)")]
    [SerializeField] private float movementResistanceSideways = 40f;
    [Tooltip("ความเร็วสูงสุดที่ผู้เล่นสามารถเคลื่อนที่ด้านข้างลมได้")]
    [SerializeField] private float maxResistSpeedSideways = 2.5f;
    [Tooltip("ถ้าติ๊ก: จะต้านแรงทุกทิศที่สวนกับลม (รวมแนวตั้งด้วย)")]
    [SerializeField] private bool resistAllOpposingMovement = false;

    [Header("Horizontal Force Only (ป้องกันการพุ่งเฉียง)")]
    [Tooltip("ถ้าติ๊ก: จะใช้เฉพาะแรงแนวนอน (ไม่ให้พุ่งขึ้น/ลง)")]
    [SerializeField] private bool horizontalForceOnly = true;
    [Tooltip("ถ้าติ๊ก: จะจำกัดความเร็วแนวตั้งเมื่ออยู่ในโซนลม")]
    [SerializeField] private bool limitVerticalVelocity = true;
    [Tooltip("ความเร็วแนวตั้งสูงสุด (ถ้า limitVerticalVelocity = true)")]
    [SerializeField] private float maxVerticalSpeed = 5f;

    [Header("Suction (ดูดเข้าศูนย์โซน)")]
    [Tooltip("แรงดูดเข้าศูนย์โซน (ช่วยป้องกันการเดินผ่าน)")]
    [SerializeField] private bool enableSuction = true;
    [SerializeField] private float suctionStrength = 15f;
    [SerializeField] private float suctionMaxRadius = 3.0f;
    [Tooltip("ถ้าติ๊ก: ดูดเข้าศูนย์เฉพาะระนาบตั้งฉากกับทิศลม")]
    [SerializeField] private bool suctionProjectOnPlane = true;

    [Header("Player Speed Limiter")]
    [Tooltip("จำกัดความเร็วสูงสุดของผู้เล่นเมื่ออยู่ในโซนลม")]
    [SerializeField] private bool limitPlayerSpeed = true;
    [Tooltip("ความเร็วสูงสุดของผู้เล่น (รวมทุกทิศ)")]
    [SerializeField] private float maxPlayerSpeed = 10f;
    [Tooltip("ความเร็วสูงสุดในการสวนทิศลม (สำหรับ limitPlayerSpeed)")]
    [SerializeField] private float maxSpeedAgainstWind = 1f;

    [Header("Wind Effects")]
    [SerializeField] private bool enableWindTurbulence = false;
    [SerializeField] private float turbulenceStrength = 1f;
    [SerializeField] private float turbulenceFrequency = 1f;
    [SerializeField] private bool enableWindGradient = false;
    [SerializeField] private float maxWindDistance = 5f;

    [Header("Platform Movement")]
    [SerializeField] private bool enablePlatformMovement = true;

    [Header("Gizmos")]
    [SerializeField] private Color gizmoColor = new Color(1f, 0.5f, 0f, 0.25f);
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
        if (pushDirection == Vector3.zero) pushDirection = Vector3.forward;
        if (pullDirection == Vector3.zero) pullDirection = Vector3.back;
        
        // Normalize และทำให้แนวนอน (ลบส่วน Y)
        pushDirection = pushDirection.normalized;
        if (horizontalForceOnly) pushDirection.y = 0f;
        pushDirection = pushDirection.normalized;
        
        pullDirection = pullDirection.normalized;
        if (horizontalForceOnly) pullDirection.y = 0f;
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
        return offMeansPull ? WindMode.Pull : WindMode.Disabled;
    }

    private void ApplyActiveLocal(WindMode mode)
    {
        UpdateParticleEffects(mode);
        UpdateUI(mode);
        UpdateWindSound(mode);
        
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
        // ป้องกัน null reference และเพิ่มการเช็คสถานะที่แม่นยำกว่า
        if (pushParticles != null)
        {
            bool shouldPlayPush = (mode == WindMode.Push);
            if (shouldPlayPush && !pushParticles.isPlaying)
            {
                // หยุด particle อื่นก่อนเล่นใหม่
                if (pushParticles.isStopped) pushParticles.Clear();
                pushParticles.Play();
            }
            else if (!shouldPlayPush && pushParticles.isPlaying)
            {
                pushParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
        
        if (pullParticles != null)
        {
            bool shouldPlayPull = (mode == WindMode.Pull);
            if (shouldPlayPull && !pullParticles.isPlaying)
            {
                // หยุด particle อื่นก่อนเล่นใหม่
                if (pullParticles.isStopped) pullParticles.Clear();
                pullParticles.Play();
            }
            else if (!shouldPlayPull && pullParticles.isPlaying)
            {
                pullParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }
    
    private void UpdateUI(WindMode mode)
    {
        _pushTweenSequence?.Kill(true);
        _pullTweenSequence?.Kill(true);
        if (pushUIRoot != null) pushUIRoot.SetActive(false);
        if (pullUIRoot != null) pullUIRoot.SetActive(false);

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
        
        // Bounce: ไปข้างหน้า > กลับ > กลับเข้าที่เดิม (ทำซ้ำ)
        Vector3 forward = targetTransform.forward;
        sequence.Append(targetTransform.DOLocalMove(originalLocalPos + forward * uiBounceDistance, uiBounceDuration * 0.35f).SetEase(Ease.OutSine));
        sequence.Append(targetTransform.DOLocalMove(originalLocalPos - forward * uiBounceDistance * 0.5f, uiBounceDuration * 0.35f).SetEase(Ease.InSine));
        sequence.Append(targetTransform.DOLocalMove(originalLocalPos, uiBounceDuration * 0.3f).SetEase(Ease.OutQuad));
        
        sequence.SetLoops(-1, LoopType.Restart).SetUpdate(UpdateType.Fixed);

        return sequence;
    }

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
        float maxSpeed = isPush ? maxPushSpeed : maxPullSpeed;
        AnimationCurve curve = isPush ? pushForceCurve : pullForceCurve;
        
        // แปลงทิศทางไปยัง world space
        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;
        Vector3 worldWind = worldRot * localDir;
        
        // ทำให้แนวนอน (ลบส่วน Y)
        if (horizontalForceOnly)
        {
            worldWind.y = 0f;
            worldWind = worldWind.normalized;
        }
        
        // คำนวณทิศทางด้านข้าง (perpendicular to wind direction)
        Vector3 worldUp = Vector3.up;
        Vector3 sidewaysDir = Vector3.Cross(worldWind, worldUp).normalized;
        
        // ถ้า worldWind เกือบจะเป็น vertical (เช่น อยู่ในแนวตั้ง) ให้ใช้ทิศทางอื่น
        if (sidewaysDir.magnitude < 0.1f)
        {
            sidewaysDir = Vector3.Cross(worldWind, Vector3.forward).normalized;
            if (sidewaysDir.magnitude < 0.1f)
            {
                sidewaysDir = Vector3.Cross(worldWind, Vector3.right).normalized;
            }
        }
        
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

            Vector3 velocity = rb.linearVelocity;
            Vector3 finalAccel = Vector3.zero;

            // 1. คำนวณแรงลมพื้นฐาน
            float currentForce = baseForce;
            if (enableWindGradient && _rigidbodyDistances.TryGetValue(rb, out float dist))
            {
                float falloff = 1f - Mathf.Clamp01(dist / Mathf.Max(0.0001f, maxWindDistance));
                currentForce *= falloff;
            }
            currentForce *= curve.Evaluate(Time.time % 1f);

            // 2. คำนวณความเร็วในทิศลม
            Vector3 alongWind = Vector3.Project(velocity, worldWind);
            float speedAlongWind = alongWind.magnitude;
            
            // 3. จำกัดความเร็วในทิศลม (ไม่ให้เร็วเกินไป)
            if (speedAlongWind > maxSpeed)
            {
                Vector3 excess = alongWind - (worldWind * maxSpeed);
                Vector3 decel = -excess * 10f; // แรงหน่วงแรงมาก
                finalAccel += decel;
            }
            else
            {
                // เพิ่มแรงลม
                finalAccel += worldWind * currentForce;
            }

            // 4. ต้านการเคลื่อนที่สวนทิศลม (Against Wind)
            if (enableResistanceAgainstWind && movementResistanceAgainst > 0f)
            {
                Vector3 velocityAgainstWind = Vector3.Project(velocity, -worldWind);
                float speedAgainstWind = velocityAgainstWind.magnitude;
                
                if (speedAgainstWind > maxResistSpeedAgainst)
                {
                    // ต้านแรงการเคลื่อนที่สวนทิศลม
                    Vector3 resistForce = -velocityAgainstWind.normalized * movementResistanceAgainst;
                    
                    if (horizontalForceOnly)
                    {
                        // ต้านเฉพาะแนวนอน
                        resistForce.y = 0f;
                    }
                    
                    finalAccel += resistForce;
                }
                else if (speedAgainstWind > 0.1f)
                {
                    // ต้านแรงเบาๆ เพื่อจำกัดความเร็ว
                    Vector3 resistForce = -velocityAgainstWind.normalized * (movementResistanceAgainst * 0.5f);
                    
                    if (horizontalForceOnly)
                    {
                        resistForce.y = 0f;
                    }
                    
                    finalAccel += resistForce;
                }
            }

            // 5. ต้านการเคลื่อนที่ด้านข้างลม (Sideways)
            if (enableResistanceSideways && movementResistanceSideways > 0f)
            {
                // คำนวณความเร็วด้านข้าง (perpendicular to wind direction)
                Vector3 velocitySideways = Vector3.Project(velocity, sidewaysDir);
                float speedSideways = velocitySideways.magnitude;
                
                // ตรวจสอบว่ามีการเคลื่อนที่ด้านข้างจริงๆ (ไม่ใช่แค่ทิศทางเดียว)
                // ใช้ Vector3.ProjectOnPlane เพื่อให้ได้เฉพาะส่วนที่ตั้งฉากกับลม
                Vector3 velocityOnPlane = Vector3.ProjectOnPlane(velocity, worldWind);
                float speedOnPlane = velocityOnPlane.magnitude;
                
                // ถ้ามีการเคลื่อนที่ในระนาบตั้งฉากกับลม
                if (speedOnPlane > maxResistSpeedSideways)
                {
                    // คำนวณทิศทางที่ตั้งฉากกับลม
                    Vector3 perpendicularVel = velocityOnPlane;
                    
                    // ต้านแรงการเคลื่อนที่ด้านข้าง
                    Vector3 resistForce = -perpendicularVel.normalized * movementResistanceSideways;
                    
                    if (horizontalForceOnly)
                    {
                        // ต้านเฉพาะแนวนอน
                        resistForce.y = 0f;
                    }
                    
                    finalAccel += resistForce;
                }
                else if (speedOnPlane > 0.1f)
                {
                    // ต้านแรงเบาๆ เพื่อจำกัดความเร็วด้านข้าง
                    Vector3 resistForce = -velocityOnPlane.normalized * (movementResistanceSideways * 0.5f);
                    
                    if (horizontalForceOnly)
                    {
                        resistForce.y = 0f;
                    }
                    
                    finalAccel += resistForce;
                }
            }

            // 6. จำกัดความเร็วแนวตั้ง (ป้องกันการพุ่งขึ้น/ลง)
            if (limitVerticalVelocity && horizontalForceOnly)
            {
                Vector3 verticalVel = new Vector3(0f, velocity.y, 0f);
                if (verticalVel.magnitude > maxVerticalSpeed)
                {
                    Vector3 verticalDamp = -verticalVel.normalized * (verticalVel.magnitude - maxVerticalSpeed) * 10f;
                    finalAccel += verticalDamp;
                }
            }

            // 7. จำกัดความเร็วรวมของผู้เล่น (ป้องกันการพุ่งเร็วเกินไป)
            if (limitPlayerSpeed)
            {
                float totalSpeed = velocity.magnitude;
                if (totalSpeed > maxPlayerSpeed)
                {
                    Vector3 speedLimitForce = -velocity.normalized * (totalSpeed - maxPlayerSpeed) * 15f;
                    finalAccel += speedLimitForce;
                }
                
                // จำกัดความเร็วในการสวนทิศลม
                Vector3 againstWind = Vector3.Project(velocity, -worldWind);
                if (againstWind.magnitude > maxSpeedAgainstWind)
                {
                    Vector3 limitAgainst = -againstWind.normalized * (againstWind.magnitude - maxSpeedAgainstWind) * 20f;
                    if (horizontalForceOnly) limitAgainst.y = 0f;
                    finalAccel += limitAgainst;
                }
            }

            // 8. แรงดูดเข้าศูนย์โซน (ช่วยป้องกันการเดินผ่าน)
            if (enableSuction)
            {
                Vector3 toCenter = zoneCenter - rb.worldCenterOfMass;
                float distToCenter = toCenter.magnitude;
                if (distToCenter > 0.001f)
                {
                    if (suctionProjectOnPlane && horizontalForceOnly)
                    {
                        // ดูดเข้าศูนย์เฉพาะระนาบแนวนอน (ไม่ดูดขึ้น/ลง)
                        toCenter.y = 0f;
                        distToCenter = toCenter.magnitude;
                    }
                    
                    if (distToCenter > 0.001f)
                    {
                        float suctionWeight = Mathf.Clamp01(1f - (distToCenter / Mathf.Max(0.0001f, suctionMaxRadius)));
                        Vector3 suctionForce = toCenter.normalized * (suctionStrength * suctionWeight);
                        
                        if (horizontalForceOnly)
                        {
                            suctionForce.y = 0f;
                        }
                        
                        finalAccel += suctionForce;
                    }
                }
            }

            // 9. Turbulence (ถ้าเปิดใช้งาน)
            if (enableWindTurbulence)
            {
                Vector3 turb = new(
                    Mathf.PerlinNoise(Time.time * turbulenceFrequency, 0f) - 0.5f,
                    horizontalForceOnly ? 0f : (Mathf.PerlinNoise(Time.time * turbulenceFrequency + 100f, 0f) - 0.5f),
                    Mathf.PerlinNoise(Time.time * turbulenceFrequency + 200f, 0f) - 0.5f);
                finalAccel += turb * turbulenceStrength;
            }

            // 10. ใช้เฉพาะแรงแนวนอน (ถ้าเปิดใช้งาน)
            if (horizontalForceOnly)
            {
                finalAccel.y = 0f;
            }

            // 11. Apply force
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

    private static void NotifyWindEnter(Rigidbody rb, PushZone sourceZone)
    {
        // (สมมติว่ามี WindContactRelay อยู่)
        // WindContactRelay relay = rb ? rb.GetComponent<WindContactRelay>() ?? rb.GetComponentInParent<WindContactRelay>() : null;
        // relay?.OnEnterZone(sourceZone);
    }

    private static void NotifyWindExit(Rigidbody rb, PushZone sourceZone)
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
            if (horizontalForceOnly) pushWorld.y = 0f;
            pushWorld = pushWorld.normalized;
            Gizmos.DrawLine(s, s + pushWorld * windArrowLength);
            Gizmos.color = Color.magenta;
            Vector3 pullWorld = (pivot ? pivot.rotation : transform.rotation) * pullDirection.normalized;
            if (horizontalForceOnly) pullWorld.y = 0f;
            pullWorld = pullWorld.normalized;
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

