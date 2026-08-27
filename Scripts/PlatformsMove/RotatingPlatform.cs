using UnityEngine;
using Unity.Netcode;
using DG.Tweening;

[RequireComponent(typeof(NetworkObject))]
public class RotatingPlatform : NetworkBehaviour, IActivatable, IWindModeActivatable
{
    public enum Axis { X, Y, Z }

    [Header("Target (Child to rotate)")]
    [SerializeField] private Transform targetChild;

    [Header("Rotation Settings")]
    [SerializeField] private Axis rotationAxis = Axis.Z;
    [SerializeField] private float rotationAngle = 360f;   // องศาต่อรอบ
    [SerializeField] private float duration = 2f;
    [SerializeField] private RotateMode rotateMode = RotateMode.FastBeyond360;

    [Header("Start State")]
    [SerializeField] private bool startActive = false;

    [Header("Wind Reaction")]
    [Tooltip("ถ้าติ๊ก: จะหมุนกลับด้านเมื่อได้รับลมแบบ Pull")]
    [SerializeField] private bool invertWhenPull = true;

    // == Networked state ==
    // ใช้ _windDir เป็น source of truth ตอนสลับทิศ (0 = หยุด, 1 = Push, -1 = Pull)
    private readonly NetworkVariable<bool> _active =
        new(writePerm: NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> _windDir =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // runtime
    private Tween _tween;
    private int _currentDir = 1; // 1 หรือ -1

    private void Awake()
    {
        if (!targetChild && transform.childCount > 0)
            targetChild = transform.GetChild(0);
    }

    public override void OnNetworkSpawn()
    {
        _active.OnValueChanged += OnActiveChanged;
        _windDir.OnValueChanged += OnWindDirChanged;

        if (IsServer)
        {
            _active.Value  = startActive;
            
            // *** การแก้ไขเพื่อให้เริ่มในโหมด Pull (หรือ Push) ตามการตั้งค่า ***
            if (startActive)
            {
                // ถ้า invertWhenPull เป็นจริง และต้องการให้เริ่มหมุนในโหมด Pull
                // Pull ถูกแมปเป็น -1 ใน SetWindMode (เมื่อ invertWhenPull = true)
                _windDir.Value = invertWhenPull ? -1 : 1; 
            }
            else
            {
                _windDir.Value = 0; 
            }
        }

        // สร้าง Tween และสั่ง Play ตามสถานะเริ่มต้นที่กำหนด
        RecreateTween(dir: _windDir.Value == 0 ? 1 : _windDir.Value);
        ApplyPlayState(_active.Value && _windDir.Value != 0);
    }

    public override void OnNetworkDespawn()
    {
        _active.OnValueChanged -= OnActiveChanged;
        _windDir.OnValueChanged -= OnWindDirChanged;
        KillTween();
    }

    // ---------- Event handlers ----------
    private void OnActiveChanged(bool prev, bool next)
    {
        if (next)
        {
            RecreateTween(_windDir.Value == 0 ? 1 : _windDir.Value);
            ApplyPlayState(_windDir.Value != 0);
        }
        else
        {
            ApplyPlayState(false);
        }
    }

    private void OnWindDirChanged(int prev, int next)
    {
        if (next == 0)
        {
            // ถ้าเป็น 0 (Disabled) ให้หยุด
            ApplyPlayState(false);
            return;
        }

        // เมื่อทิศทางลมเปลี่ยน (เป็น 1 หรือ -1) ให้สร้าง Tween ใหม่ตามทิศทางนั้น
        RecreateTween(dir: next);
        
        // สั่งเล่นทันทีเมื่อทิศทางลมเปลี่ยน หาก Active อยู่
        if (_active.Value)
        {
            ApplyPlayState(true); 
        }
    }

    // ---------- Core tween control ----------
    private void RecreateTween(int dir)
    {
        _currentDir = Mathf.Sign(dir) == -1 ? -1 : 1;

        if (!targetChild)
        {
            Debug.LogWarning($"[{name}] RotatingPlatform: targetChild ว่าง");
            return;
        }

        Vector3 fromEuler = targetChild.localEulerAngles;
        KillTween();

        float rotationAmount = rotationAngle * _currentDir;

        Vector3 perLoop = rotationAxis switch
        {
            Axis.X => new Vector3(rotationAmount, 0f, 0f),
            Axis.Y => new Vector3(0f, rotationAmount, 0f),
            _      => new Vector3(0f, 0f, rotationAmount),
        };

        _tween = targetChild
            .DOLocalRotate(perLoop, duration, rotateMode)
            .SetEase(Ease.Linear)
            .SetRelative(true)
            .SetLoops(-1, LoopType.Incremental)
            .SetId(gameObject)
            .SetTarget(targetChild);

        targetChild.localEulerAngles = fromEuler;
        
        // สั่งเล่น Tween ใหม่ทันที หากสถานะโดยรวมควรเป็น Active
        if (_active.Value && _currentDir != 0)
        {
            _tween.Play();
        }
    }

    private void KillTween()
    {
        if (_tween != null)
        {
            if (_tween.IsActive()) _tween.Kill(false);
            _tween = null;
        }
    }

    private void ApplyPlayState(bool shouldPlay)
    {
        if (_tween == null)
        {
            if (shouldPlay)
            {
                RecreateTween(_windDir.Value == 0 ? 1 : _windDir.Value);
            }
            return;
        }

        if (shouldPlay) _tween.Play();
        else _tween.Pause();
    }

    // ---------- IActivatable ----------
    public void Activate(bool on)
    {
        if (!IsServer) return;
        _active.Value = on;
    }

    // ---------- IWindModeActivatable ----------
    public void SetWindMode(WindMode mode)
    {
        if (!IsServer) return;

        if (!invertWhenPull)
        {
            switch (mode)
            {
                case WindMode.Push:
                case WindMode.Pull:  _windDir.Value = 1;  _active.Value = true;  break;
                default:             _windDir.Value = 0;  _active.Value = false; break;
            }
            return;
        }

        // ตั้งค่าลำดับ: _windDir มาก่อน _active
        switch (mode)
        {
            case WindMode.Push: _windDir.Value =  1; _active.Value = true;  break;
            case WindMode.Pull: _windDir.Value = -1; _active.Value = true;  break;
            default:            _windDir.Value =  0; _active.Value = false; break;
        }
    }
}