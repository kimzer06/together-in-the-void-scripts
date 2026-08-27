using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using DG.Tweening;

/// <summary>
/// โซนลมร้อนที่สร้างความเสียหาย/ฆ่าผู้เล่นที่เข้ามาในพื้นที่
/// สถานะจะถูกซิงค์ผ่านเน็ตเวิร์คและแสดงผลด้วย Particle Effects และ UI
/// สามารถเปิด/ปิดได้ผ่าน IActivatable (สวิช) หรือ IWindModeActivatable (WindModeSwitcher)
/// - เมื่อ WindMode เป็น Push/Pull: เปิดโซน (แสดง UI, เล่น Particle, ตรวจจับผู้เล่น)
/// - เมื่อ WindMode เป็น Disabled: ปิดโซน (ซ่อน UI, หยุด Particle, ไม่ตรวจจับผู้เล่น)
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[AddComponentMenu("WindZone/Heat Death Zone")]
public class HeatDeathZone : NetworkBehaviour, IActivatable, IWindModeActivatable, IFreezeListener
{
    #region Inspector Fields & Enums
    private enum DetectionShape { Box, Sphere, Capsule, Mesh, Cylinder }

    [Header("State (initial on Server)")]
    [SerializeField] private bool defaultActive = true;

    [Header("Player Detection")]
    [Tooltip("Tag ของผู้เล่น (เช่น 'Player') - ถ้าว่างจะตรวจสอบ IsPlayerObject แทน")]
    [SerializeField] private string playerTag = "Player";
    [Tooltip("ถ้าติ๊ก: ใช้ NetworkObject.IsPlayerObject ในการตรวจสอบผู้เล่น (แนะนำ)")]
    [SerializeField] private bool useNetworkPlayerCheck = true;

    [Header("Effects")]
    [Tooltip("GameObject ที่มี Particle System หลายตัวที่จะเล่นเมื่อโซนเปิดอยู่ (ลมร้อน) - จะเล่น/หยุด Particle Systems ทั้งหมดใน GameObject นี้")]
    [SerializeField] private GameObject heatParticles;
    
    [Header("Particle Delay Settings")]
    [Tooltip("ระยะเวลาหลังจากเปิด Particle ก่อนที่จะเปิด DeadZone (วินาที)")]
    [SerializeField] private float particleStartDelay = 0f;
    [Tooltip("ระยะเวลาหลังจากหยุด Particle ก่อนที่จะปิด DeadZone (วินาที)")]
    [SerializeField] private float particleStopDelay = 0f;
    
    [Header("UI")]
    [Tooltip("UI/GameObject ที่จะแสดงเมื่อโซนเปิดอยู่ (เช่น ไอคอนเตือน, ข้อความ)")]
    [SerializeField] private GameObject activeUI;

    [Header("Pivot Settings")]
    [SerializeField] private Transform pivot;

    [Header("Detection Settings")]
    [SerializeField] private Vector3 detectPosition = Vector3.zero;
    [SerializeField] private DetectionShape detectionShape = DetectionShape.Box;
    [SerializeField] private Mesh meshPreview;
    [SerializeField] private LayerMask detectLayers = ~0;

    [Header("Box Settings")] 
    [SerializeField] private Vector3 boxSize = Vector3.one;
    
    [Header("Sphere Settings")] 
    [SerializeField] private float sphereRadius = 0.5f;
    
    [Header("Capsule Settings")] 
    [SerializeField] private float capsuleRadius = 0.5f;
    [SerializeField] private float capsuleHeight = 2f;
    
    [Header("Cylinder Settings")] 
    [SerializeField] private float cylinderRadius = 0.5f;
    [SerializeField] private float cylinderHeight = 2f;

    [Header("Slide zone death (optional)")]
    [Tooltip("ถ้าเปิดและลาก SplineSlideZone — ผู้เล่นที่กำลังสไลด์อยู่จะตายแบบ SlideZone แทน Kill() ทั่วไป")]
    [SerializeField] private bool useSlideZoneObstacleDeath = false;
    [SerializeField] private SplineSlideZone slideSplineZone;
    
    [Header("Death Settings")]
    [Tooltip("ระยะเวลาระหว่างการตรวจสอบผู้เล่น (วินาที) - ตั้งค่าน้อยเพื่อตอบสนองเร็ว")]
    [SerializeField] private float checkInterval = 0.1f;
    [Tooltip("ถ้าติ๊ก: จะฆ่าเฉพาะครั้งแรกที่เข้ามา (ไม่ฆ่าซ้ำ)")]
    [SerializeField] private bool killOncePerEntry = false;

    [Header("Gizmos")]
    [SerializeField] private Color gizmoColor = new Color(1f, 0.3f, 0f, 0.25f);
    [SerializeField] private bool showGizmo = true;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อลมเริ่มทำงาน")]
    [SerializeField] private AudioClip windSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;
    [Tooltip("ระยะเวลาที่ใช้ในการ fade out เสียงเมื่อเปลี่ยนโหมด (วินาที)")]
    [SerializeField, Min(0f)] private float soundFadeOutDuration = 0.5f;
    
    [Header("Heat Emission Effect")]
    [Tooltip("GameObject ที่จะปรับ emission ให้เป็นสีแดงร้อน (จะค้นหา Renderer ทั้งหมดใน GameObject นี้)")]
    [SerializeField] private GameObject heatEmissionTarget;
    [Tooltip("สี emission ปกติ (สีเริ่มต้น)")]
    [SerializeField] private Color normalEmissionColor = Color.black;
    [Tooltip("สี emission เมื่อร้อน (สีแดงแจ๋)")]
    [SerializeField] private Color hotEmissionColor = new Color(1f, 0.1f, 0f, 1f);
    [Tooltip("Intensity ของ emission เมื่อร้อน (ยิ่งมากยิ่งสว่าง)")]
    [SerializeField, Min(0f)] private float hotEmissionIntensity = 2f;
    [Tooltip("ระยะเวลาที่ใช้ในการเปลี่ยนสี emission (วินาที)")]
    [SerializeField, Min(0f)] private float emissionTransitionDuration = 1f;
    #endregion

    #region Runtime State
    private readonly NetworkVariable<bool> _zoneActiveNV = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _deadZoneActiveNV = new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly HashSet<NetworkObject> _trackedPlayers = new();
    private float _lastCheckTime;
    private Coroutine _activationCoroutine;
    private Tween _audioFadeTween;
    
    // Time Freeze
    private bool _isFrozen;
    private bool _stateBeforeFreeze;
    private ParticleSystem[] _cachedParticles;
    
    // Heat Emission
    private Renderer[] _heatRenderers;
    private Material[][] _heatMaterials; // เก็บ material instances ของแต่ละ renderer
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
    private static readonly int EmissiveIntensityId = Shader.PropertyToID("_EmissiveIntensity");
    private static readonly string EmissionKeyword = "_EMISSION";
    private static readonly string HDRPEmissiveKeyword = "_EMISSIVE";
    private Tween _emissionTween;
    private Color _currentEmissionColor;
    private float _currentEmissionIntensity;
    
    // Client-side instant slide zone freeze
    private bool _localPlayerFrozenForSlide = false;
    #endregion

    #region Unity Lifecycle
    private void Awake()
    {
        if (!pivot) pivot = transform;
        if (heatParticles != null)
            _cachedParticles = heatParticles.GetComponentsInChildren<ParticleSystem>();
        InitializeAudio();
        InitializeHeatEmission();
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
    
    private void InitializeHeatEmission()
    {
        if (heatEmissionTarget == null) return;
        
        // ค้นหา Renderer ทั้งหมดใน GameObject (รวม children)
        _heatRenderers = heatEmissionTarget.GetComponentsInChildren<Renderer>();
        
        if (_heatRenderers == null || _heatRenderers.Length == 0) return;
        
        // เก็บ material instances ของแต่ละ renderer
        _heatMaterials = new Material[_heatRenderers.Length][];
        for (int i = 0; i < _heatRenderers.Length; i++)
        {
            if (_heatRenderers[i] == null) continue;
            
            // ใช้ materials (instance) แทน sharedMaterials เพื่อให้แก้ไขได้
            _heatMaterials[i] = _heatRenderers[i].materials;
            
            // Enable emission keyword และตั้งค่า globalIlluminationFlags สำหรับแต่ละ material
            foreach (Material mat in _heatMaterials[i])
            {
                if (mat == null) continue;
                
                // Enable emission keyword สำหรับ Standard shader
                if (mat.HasProperty(EmissionColorId))
                {
                    mat.EnableKeyword(EmissionKeyword);
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.AnyEmissive;
                }
                
                // Enable emission keyword สำหรับ HDRP shader
                if (mat.HasProperty(EmissiveColorId))
                {
                    mat.EnableKeyword(HDRPEmissiveKeyword);
                }
            }
        }
        
        // ตั้งค่า emission เริ่มต้นเป็นสีปกติ
        _currentEmissionColor = normalEmissionColor;
        _currentEmissionIntensity = 0f;
        
        ApplyEmissionColor(normalEmissionColor, 0f);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            _zoneActiveNV.Value = defaultActive;
            _deadZoneActiveNV.Value = defaultActive;
        }
        
        // อัปเดต UI ตาม DeadZone state เมื่อเริ่มต้น
        UpdateUIVisibility(_deadZoneActiveNV.Value);
        
        ApplyActiveLocal(_zoneActiveNV.Value);
        _lastCheckTime = Time.time;
    }

    private void OnEnable() 
    { 
        _zoneActiveNV.OnValueChanged += OnZoneActiveChanged;
        _deadZoneActiveNV.OnValueChanged += OnDeadZoneActiveChanged;
    }
    
    private void OnDisable() 
    { 
        _zoneActiveNV.OnValueChanged -= OnZoneActiveChanged;
        _deadZoneActiveNV.OnValueChanged -= OnDeadZoneActiveChanged;
        
        // หยุด Coroutine ถ้ามี
        if (_activationCoroutine != null)
        {
            StopCoroutine(_activationCoroutine);
            _activationCoroutine = null;
        }
        
        // หยุด fade tween ถ้ามี
        _audioFadeTween?.Kill();
        
        // หยุด emission tween และคืนสีกลับเป็นปกติ
        _emissionTween?.Kill();
        if (_heatMaterials != null)
        {
            ApplyEmissionColor(normalEmissionColor, 0f);
        }
        
        // หยุด Particle เมื่อถูกปิด
        if (_cachedParticles != null)
        {
            foreach (ParticleSystem ps in _cachedParticles)
            {
                if (ps != null && ps.isPlaying)
                    ps.Stop();
            }
        }
        
        // หยุดเสียงเมื่อถูกปิด
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        
        // ซ่อน UI เมื่อถูกปิด
        if (activeUI != null)
        {
            activeUI.SetActive(false);
        }
    }

    private void Update()
    {
        if (_isFrozen) return;
        if (!_deadZoneActiveNV.Value)
        {
            // รีเซ็ต flag เมื่อโซนปิด เพื่อให้ทำงานได้ใหม่เมื่อเปิดอีกครั้ง
            _localPlayerFrozenForSlide = false;
            if (IsServer) return;
            return;
        }
        
        // ★ CLIENT-SIDE: ตรวจจับ local player ที่กำลังสไลด์เข้ามาในโซน → freeze ทันที
        // เพื่อให้ตายแบบไม่ดีเลย์ (เหมือน SlideZoneHitbox ที่ใช้ OnTriggerEnter ฝั่ง client)
        if (!IsServer && useSlideZoneObstacleDeath && slideSplineZone != null && !_localPlayerFrozenForSlide)
        {
            ClientSideSlideDeathCheck();
        }
        
        if (!IsServer) return;

        if (Time.time - _lastCheckTime >= checkInterval)
        {
            _lastCheckTime = Time.time;
            DetectAndKillPlayers();
        }
    }
    
    /// <summary>
    /// ฝั่ง Client: ตรวจสอบว่า local player ที่กำลังสไลด์อยู่เข้ามาใน dead zone หรือไม่
    /// ถ้าใช่ → เรียก Client_DisableForSlideZoneLocal() ทันที (ไม่ต้องรอ server RPC กลับ)
    /// Server จะจัดการ death/respawn ผ่าน DetectAndKillPlayers() ตามปกติ
    /// </summary>
    private void ClientSideSlideDeathCheck()
    {
        // หา local player ที่กำลังสไลด์อยู่
        if (NetworkManager.Singleton == null) return;
        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(localClientId, out var client)) return;
        var playerObj = client.PlayerObject;
        if (playerObj == null) return;
        
        var death = playerObj.GetComponent<PlayerDeath>();
        if (death == null) return;
        if (death.IsRespawnImmune) return;
        if (death.IsSlideZoneDeathInProgress) return;
        if (death.IsHiddenState) return;
        
        // เช็คว่า local player อยู่ใน active slide หรือไม่
        if (!slideSplineZone.IsPlayerInActiveSlide(localClientId)) return;
        
        // ตรวจสอบว่า local player collider อยู่ในโซนหรือไม่
        Collider[] detected = GetOverlaps();
        if (detected == null || detected.Length == 0) return;
        
        bool localPlayerInZone = false;
        foreach (Collider c in detected)
        {
            if (!c) continue;
            var netObj = c.GetComponentInParent<NetworkObject>();
            if (netObj != null && netObj == playerObj)
            {
                localPlayerInZone = true;
                break;
            }
        }
        
        if (!localPlayerInZone) return;
        
        // ★ Freeze ทันทีบน client (เหมือน SlideZoneHitbox)
        _localPlayerFrozenForSlide = true;
        death.Client_DisableForSlideZoneLocal();
        
        // ส่ง RPC ไป server เพื่อให้ server จัดการ slide death + respawn
        slideSplineZone.RequestSlideOrNormalDeathFromHazardTrigger();
        
        Debug.Log($"[HeatDeathZone] Client-side instant freeze for slide zone death (player {localClientId}).");
    }
    #endregion

    #region Public Interface Implementation
    public void Activate(bool on)
    {
        if (IsServer) 
        {
            _zoneActiveNV.Value = on;
        }
        else 
        {
            RequestSetActiveServerRpc(on);
        }
    }

    public void SetWindMode(WindMode mode)
    {
        // Push หรือ Pull = เปิดโซน, Disabled = ปิดโซน
        bool shouldActivate = mode == WindMode.Push || mode == WindMode.Pull;
        Activate(shouldActivate);
    }
    #endregion

    #region Network Logic
    [ServerRpc(RequireOwnership = false)]
    private void RequestSetActiveServerRpc(bool on) => _zoneActiveNV.Value = on;
    
    private void OnZoneActiveChanged(bool prev, bool next) => ApplyActiveLocal(next);
    
    private void OnDeadZoneActiveChanged(bool prev, bool next)
    {
        // อัปเดต UI ตาม DeadZone state
        UpdateUIVisibility(next);
        
        if (!next)
        {
            // เคลียร์รายการผู้เล่นที่ติดตามเมื่อ DeadZone ถูกปิด
            _trackedPlayers.Clear();
        }
    }
    #endregion

    #region Core Logic
    private void ApplyActiveLocal(bool isActive)
    {
        if (_isFrozen)
        {
            _stateBeforeFreeze = isActive;
            return;
        }

        if (_activationCoroutine != null)
        {
            StopCoroutine(_activationCoroutine);
            _activationCoroutine = null;
        }
        
        UpdateWindSound(isActive);
        _activationCoroutine = StartCoroutine(ActivationSequence(isActive));
    }
    
    private IEnumerator ActivationSequence(bool isActive)
    {
        if (isActive)
        {
            // กรณีเปิด: เริ่มร้อนทันที → เปิด Particle → รอ delay → เปิด DeadZone
            UpdateHeatEmission(true);
            UpdateParticleEffects(true);
            
            if (particleStartDelay > 0f)
            {
                yield return new WaitForSeconds(particleStartDelay);
            }
            
            // เปิด DeadZone (เฉพาะ Server)
            if (IsServer)
            {
                _deadZoneActiveNV.Value = true;
            }
        }
        else
        {
            // กรณีปิด: หยุด Particle ก่อน → รอ delay → คืน emission → ปิด DeadZone
            UpdateParticleEffects(false);
            
            if (particleStopDelay > 0f)
            {
                yield return new WaitForSeconds(particleStopDelay);
            }
            
            // คืน emission กลับเป็นสีปกติ
            UpdateHeatEmission(false);
            
            // ปิด DeadZone หลังจาก delay
            if (IsServer)
            {
                _deadZoneActiveNV.Value = false;
            }
        }
        
        _activationCoroutine = null;
    }
    
    private void UpdateUIVisibility(bool isActive)
    {
        if (activeUI != null)
        {
            activeUI.SetActive(isActive);
        }
    }

    private void UpdateParticleEffects(bool isActive)
    {
        if (_cachedParticles == null || _cachedParticles.Length == 0) return;

        foreach (ParticleSystem ps in _cachedParticles)
        {
            if (ps == null) continue;

            if (isActive && !ps.isPlaying)
            {
                if (ps.isStopped) ps.Clear();
                ps.Play();
            }
            else if (!isActive && ps.isPlaying)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }

    private void UpdateWindSound(bool isActive)
    {
        if (audioSource == null || windSound == null) return;

        // หยุด fade tween เดิมถ้ามี
        _audioFadeTween?.Kill();

        // ถ้าเปิดใช้งานให้เล่นเสียง
        if (isActive)
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
            // ถ้าปิดใช้งานให้ fade out
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
    
    private void UpdateHeatEmission(bool isHot)
    {
        if (_heatMaterials == null || _heatMaterials.Length == 0) return;
        
        // หยุด tween เดิมถ้ามี
        _emissionTween?.Kill();
        
        Color targetColor = isHot ? hotEmissionColor : normalEmissionColor;
        float targetIntensity = isHot ? hotEmissionIntensity : 0f;
        
        // ใช้ค่าปัจจุบันที่เก็บไว้
        Color startColor = _currentEmissionColor;
        float startIntensity = _currentEmissionIntensity;
        
        // สร้าง tween สำหรับเปลี่ยนสี
        float t = 0f;
        _emissionTween = DOTween.To(() => t, x => t = x, 1f, emissionTransitionDuration)
            .OnUpdate(() =>
            {
                Color lerpedColor = Color.Lerp(startColor, targetColor, t);
                float lerpedIntensity = Mathf.Lerp(startIntensity, targetIntensity, t);
                _currentEmissionColor = lerpedColor;
                _currentEmissionIntensity = lerpedIntensity;
                ApplyEmissionColor(lerpedColor, lerpedIntensity);
            })
            .OnComplete(() =>
            {
                _currentEmissionColor = targetColor;
                _currentEmissionIntensity = targetIntensity;
                ApplyEmissionColor(targetColor, targetIntensity);
            });
    }
    
    private void ApplyEmissionColor(Color color, float intensity)
    {
        if (_heatMaterials == null || _heatRenderers == null) return;
        
        for (int i = 0; i < _heatRenderers.Length; i++)
        {
            if (_heatRenderers[i] == null || _heatMaterials[i] == null) continue;
            
            foreach (Material mat in _heatMaterials[i])
            {
                if (mat == null) continue;
                
                // รองรับทั้ง Standard และ HDRP shader
                if (mat.HasProperty(EmissionColorId))
                {
                    // Standard shader: _EmissionColor (HDR color)
                    mat.SetColor(EmissionColorId, color * intensity);
                }
                
                if (mat.HasProperty(EmissiveColorId))
                {
                    // HDRP shader: _EmissiveColor + _EmissiveIntensity
                    mat.SetColor(EmissiveColorId, color);
                    if (mat.HasProperty(EmissiveIntensityId))
                    {
                        mat.SetFloat(EmissiveIntensityId, intensity);
                    }
                }
            }
        }
    }

    private void DetectAndKillPlayers()
    {
        Collider[] detected = GetOverlaps();
        if (detected == null || detected.Length == 0)
        {
            // ถ้าไม่มีอะไรในโซน ให้เคลียร์รายการ
            if (killOncePerEntry)
            {
                _trackedPlayers.Clear();
            }
            return;
        }

        // ตรวจสอบผู้เล่นที่เข้ามาใหม่
        foreach (Collider c in detected)
        {
            if (!c) continue;

            // ตรวจสอบว่าเป็นผู้เล่นหรือไม่
            if (!IsPlayer(c, out NetworkObject playerNO)) continue;

            // ถ้าใช้ killOncePerEntry และเคยฆ่าไปแล้ว ข้าม
            if (killOncePerEntry && _trackedPlayers.Contains(playerNO)) continue;

            // ฆ่าผู้เล่น
            PlayerDeath death = playerNO.GetComponent<PlayerDeath>();
            if (death != null)
            {
                // ★ ข้ามผู้เล่นที่อยู่ในช่วง respawn immunity (เช่น เพิ่ง respawn ที่เพื่อนบน Slide Zone)
                if (death.IsRespawnImmune) continue;
                
                // ★ ข้ามผู้เล่นที่ตายไปแล้ว (ซ่อนอยู่) — กัน Kill() ซ้ำหลัง serverIsProcessingDeath หมดอายุ
                if (death.IsHiddenState) continue;
                
                bool handledBySlide = useSlideZoneObstacleDeath && slideSplineZone != null && slideSplineZone.IsSpawned
                    && slideSplineZone.TryServerHandleSlideObstacleDeath(playerNO.OwnerClientId);
                if (!handledBySlide)
                    death.Kill();
                
                if (killOncePerEntry)
                {
                    _trackedPlayers.Add(playerNO);
                }
            }
        }

        // ลบผู้เล่นที่ไม่อยู่ในโซนแล้วออกจากรายการ (สำหรับ killOncePerEntry)
        if (killOncePerEntry)
        {
            HashSet<NetworkObject> toRemove = new();
            foreach (NetworkObject trackedNO in _trackedPlayers)
            {
                if (trackedNO == null || !trackedNO.IsSpawned)
                {
                    toRemove.Add(trackedNO);
                    continue;
                }

                bool stillInside = false;
                foreach (Collider c in detected)
                {
                    if (!c) continue;
                    NetworkObject detectedNO = c.GetComponentInParent<NetworkObject>();
                    if (detectedNO == trackedNO)
                    {
                        stillInside = true;
                        break;
                    }
                }

                if (!stillInside)
                {
                    toRemove.Add(trackedNO);
                }
            }

            foreach (NetworkObject removeNO in toRemove)
            {
                _trackedPlayers.Remove(removeNO);
            }
        }
    }

    private bool IsPlayer(Collider c, out NetworkObject playerNO)
    {
        playerNO = null;

        // วิธีที่ 1: ตรวจสอบด้วย NetworkObject.IsPlayerObject (แนะนำ)
        if (useNetworkPlayerCheck)
        {
            playerNO = c.GetComponentInParent<NetworkObject>();
            if (playerNO != null && playerNO.IsPlayerObject)
            {
                return true;
            }
        }

        // วิธีที่ 2: ตรวจสอบด้วย Tag
        if (!string.IsNullOrEmpty(playerTag) && c.CompareTag(playerTag))
        {
            playerNO = c.GetComponentInParent<NetworkObject>();
            if (playerNO != null)
            {
                return true;
            }
        }

        return false;
    }

    private Collider[] GetOverlaps()
    {
        Vector3 worldPos = pivot ? pivot.TransformPoint(detectPosition) : transform.TransformPoint(detectPosition);
        Quaternion worldRot = pivot ? pivot.rotation : transform.rotation;
        Vector3 up = pivot ? pivot.up : Vector3.up;

        switch (detectionShape)
        {
            case DetectionShape.Box: 
                return Physics.OverlapBox(worldPos, boxSize * 0.5f, worldRot, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Sphere: 
                return Physics.OverlapSphere(worldPos, sphereRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Capsule:
                float hh = Mathf.Max(0, (capsuleHeight * 0.5f) - capsuleRadius);
                Vector3 a = worldPos + up * hh;
                Vector3 b = worldPos - up * hh;
                return Physics.OverlapCapsule(a, b, capsuleRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            case DetectionShape.Cylinder:
                float half = cylinderHeight * 0.5f;
                Vector3 top = worldPos + up * half;
                Vector3 bottom = worldPos - up * half;
                return Physics.OverlapCapsule(top, bottom, cylinderRadius, detectLayers, QueryTriggerInteraction.Collide);
            
            default: 
                return null;
        }
    }
    #endregion

    #region IFreezeListener
    public void OnFreezeChanged(bool on)
    {
        if (on)
            Freeze();
        else
            Unfreeze();
    }

    private void Freeze()
    {
        _isFrozen = true;
        _stateBeforeFreeze = _zoneActiveNV.Value;

        StopParticlesGracefully();
        UpdateWindSound(false);
    }

    private void Unfreeze()
    {
        _isFrozen = false;

        if (_stateBeforeFreeze)
        {
            UpdateParticleEffects(true);
            UpdateWindSound(true);
        }
    }

    private void StopParticlesGracefully()
    {
        if (_cachedParticles == null) return;
        foreach (ParticleSystem ps in _cachedParticles)
        {
            if (ps != null && ps.isPlaying)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }
    #endregion

    #region Gizmos
    private void OnDrawGizmos()
    {
        if (!showGizmo) return;
        if (!pivot) pivot = transform;
        
        Gizmos.color = gizmoColor;
        Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
        
        switch (detectionShape)
        {
            case DetectionShape.Box: 
                Gizmos.DrawCube(detectPosition, boxSize); 
                Gizmos.DrawWireCube(detectPosition, boxSize); 
                break;
            
            case DetectionShape.Sphere: 
                Gizmos.DrawSphere(detectPosition, sphereRadius); 
                Gizmos.DrawWireSphere(detectPosition, sphereRadius); 
                break;
            
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
            
            case DetectionShape.Cylinder: 
                DrawWireCylinder(detectPosition, cylinderRadius, cylinderHeight, 32); 
                break;
            
            case DetectionShape.Mesh:
                if (meshPreview)
                {
                    Gizmos.DrawMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);
                    Gizmos.color = new Color(0f, 0f, 0f, gizmoColor.a + 0.1f);
                    Gizmos.DrawWireMesh(meshPreview, 0, detectPosition, Quaternion.identity, Vector3.one);
                }
                break;
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

