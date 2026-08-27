using UnityEngine;
using Unity.Netcode;
using UnityEngine.Audio;
using DG.Tweening;

[RequireComponent(typeof(NetworkObject))]
public class PlatformAxisRotatorActivatableNet : NetworkBehaviour, IActivatable
{
    public enum Axis { X, Y, Z }

    [Header("Target (Transform to rotate)")]
    [SerializeField] private Transform target;

    [Header("Rotation Forward (when pressed)")]
    [SerializeField] private Axis rotateAxis = Axis.Z;
    [SerializeField] private float rotateAngle = 45f;
    [SerializeField, Min(0f)] private float forwardDuration = 0.6f;
    [SerializeField] private Ease forwardEase = Ease.OutCubic;
    [SerializeField, Min(0f)] private float holdDuration = 0f;

    [Header("Rotation Back (when released)")]
    [SerializeField, Min(0f)] private float returnDuration = 0.6f;
    [SerializeField] private Ease returnEase = Ease.InCubic;

    [Header("Space / Mode")]
    [SerializeField] private bool useLocalSpace = true;
    [SerializeField] private RotateMode rotateMode = RotateMode.Fast;
    [SerializeField] private bool returnOnRelease = true;

    [Header("Start State")] 
    [SerializeField] private bool startPressed = false;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อ Platform เริ่มขยับ")]
    [SerializeField] private AudioClip movementSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    // cached
    private Vector3 _startLocalEuler;
    private Vector3 _targetLocalEuler;
    private Quaternion _startWorldRot;
    private Quaternion _targetWorldRot;
    private Tween _tween;
    private Tween _holdTween;
    private bool _isPressed;

    private void Awake()
    {
        if (!target) target = transform;
        CacheStartRotations();
        RecalculateTargetRotations();
        InitializeAudio();
    }

    private void InitializeAudio()
    {
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = true; // เสียงขยับ Platform ควรเป็น loop
            if (outputMixerGroup != null)
            {
                audioSource.outputAudioMixerGroup = outputMixerGroup;
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        SetInstant(startPressed);
        _isPressed = startPressed;
    }

    public override void OnNetworkDespawn()
    {
        _tween?.Kill();
        _tween = null;
        _holdTween?.Kill();
        _holdTween = null;
    }

    private void CacheStartRotations()
    {
        _startLocalEuler = target.localEulerAngles;
        _startWorldRot = target.rotation;
    }

    private void RecalculateTargetRotations()
    {
        Vector3 axisVec = rotateAxis == Axis.X ? new Vector3(rotateAngle, 0f, 0f)
                          : rotateAxis == Axis.Y ? new Vector3(0f, rotateAngle, 0f)
                          : new Vector3(0f, 0f, rotateAngle);

        _targetLocalEuler = _startLocalEuler + axisVec;

        Quaternion delta = Quaternion.Euler(axisVec);
        _targetWorldRot = _startWorldRot * delta;
    }

    // ========== IActivatable ==========
    public void Activate(bool on)
    {
        if (!IsServer) return;
        if (_isPressed == on) return;

        _tween?.Kill();
        _holdTween?.Kill();

        // If released and returnOnRelease is false, just stop at current position
        if (!on && !returnOnRelease)
        {
            _isPressed = on;
            return;
        }

        if (useLocalSpace)
        {
            Vector3 to = on ? _targetLocalEuler : _startLocalEuler;
            float dur = on ? forwardDuration : returnDuration;
            Ease ease = on ? forwardEase : returnEase;
            _tween = target.DOLocalRotate(to, dur, rotateMode).SetEase(ease);
            
            // เล่นเสียงเมื่อเริ่มขยับ
            PlayMovementSoundClientRpc();
            
            // If moving forward and holdDuration > 0, wait then auto-return (if returnOnRelease is true)
            if (on && holdDuration > 0f && returnOnRelease)
            {
                _tween.OnComplete(() =>
                {
                    // หยุดเสียงเมื่อเสร็จ
                    StopMovementSoundClientRpc();
                    
                    if (_isPressed && returnOnRelease)
                    {
                        _holdTween = DOVirtual.DelayedCall(holdDuration, () =>
                        {
                            if (_isPressed && returnOnRelease)
                            {
                                Activate(false);
                            }
                        });
                    }
                });
            }
            else
            {
                // หยุดเสียงเมื่อเสร็จ (กรณีไม่มี auto-return)
                _tween.OnComplete(() => StopMovementSoundClientRpc());
            }
        }
        else
        {
            Quaternion to = on ? _targetWorldRot : _startWorldRot;
            float dur = on ? forwardDuration : returnDuration;
            Ease ease = on ? forwardEase : returnEase;
            _tween = target.DORotateQuaternion(to, dur).SetEase(ease);
            
            // เล่นเสียงเมื่อเริ่มขยับ
            PlayMovementSoundClientRpc();
            
            // If moving forward and holdDuration > 0, wait then auto-return (if returnOnRelease is true)
            if (on && holdDuration > 0f && returnOnRelease)
            {
                _tween.OnComplete(() =>
                {
                    // หยุดเสียงเมื่อเสร็จ
                    StopMovementSoundClientRpc();
                    
                    if (_isPressed && returnOnRelease)
                    {
                        _holdTween = DOVirtual.DelayedCall(holdDuration, () =>
                        {
                            if (_isPressed && returnOnRelease)
                            {
                                Activate(false);
                            }
                        });
                    }
                });
            }
            else
            {
                // หยุดเสียงเมื่อเสร็จ (กรณีไม่มี auto-return)
                _tween.OnComplete(() => StopMovementSoundClientRpc());
            }
        }

        _isPressed = on;
    }

    [ClientRpc]
    private void PlayMovementSoundClientRpc()
    {
        if (audioSource != null && movementSound != null)
        {
            audioSource.clip = movementSound;
            audioSource.Play();
        }
    }

    [ClientRpc]
    private void StopMovementSoundClientRpc()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
    }

    private void SetInstant(bool pressed)
    {
        _tween?.Kill();
        if (useLocalSpace)
        {
            target.localEulerAngles = pressed ? _targetLocalEuler : _startLocalEuler;
        }
        else
        {
            target.rotation = pressed ? _targetWorldRot : _startWorldRot;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!target) target = transform;
        CacheStartRotations();
        RecalculateTargetRotations();
    }

    private void OnDrawGizmosSelected()
    {
        if (!target) target = transform;
        // Draw an arc direction indicator roughly
        Gizmos.color = Color.yellow;
        Vector3 pos = target.position;
        Vector3 axis = rotateAxis == Axis.X ? target.right : rotateAxis == Axis.Y ? target.up : target.forward;
        Gizmos.DrawRay(pos, axis * 0.5f);
    }
#endif
}


