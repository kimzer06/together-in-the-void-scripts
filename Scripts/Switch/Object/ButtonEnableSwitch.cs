using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(Collider))]
public class ButtonEnableSwitchDirect : NetworkBehaviour
{
    [Serializable]
    public class TargetEntry
    {
        [Tooltip("คอมโพเนนต์ที่ implement IActivatable")]
        public MonoBehaviour activatableComponent;

        [Tooltip("ติ๊ก = อนุญาตให้ชิ้นนี้เล่นเมื่อกด / ไม่ติ๊ก = ไม่สั่งชิ้นนี้")]
        public bool allow = true;

        [Tooltip("กลับค่าความจริง (true→false)")]
        public bool invert = false;
    }

    public enum PressMode { Toggle, ForceOn, ForceOff }

    [Header("Who can press")]
    [SerializeField] private string playerTag = "Player";

    [Header("Input / UI")]
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;
    [SerializeField] private GameObject promptUI;

    [Header("Mode")]
    [SerializeField] private PressMode pressMode = PressMode.Toggle;

    [Header("Targets")]
    [SerializeField] private List<TargetEntry> targets = new();

    [Header("Anti-spam")]
    [SerializeField] private float pressCooldown = 0.12f;
    float _lastPress;

    // ===== runtime state (server-authoritative) =====
    // เก็บสถานะล่าสุดในปุ่มนี้ (ถ้าเลือก Toggle จะกลับไป-กลับมาเมื่อกด)
    private bool _currentEnabledState = false;

    // eligibility
    ulong? _eligibleClientId;
    bool _isLocalEligible;

    void Awake()
    {
        if (promptUI) promptUI.SetActive(false);
    }

    void Update()
    {
        if (!IsClient || !_isLocalEligible) return;

        bool pressed = false;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null) pressed = Keyboard.current.eKey.wasPressedThisFrame;
#else
        pressed = Input.GetKeyDown(fallbackKey);
#endif
        if (!pressed) return;

        if (Time.time - _lastPress < pressCooldown) return;
        _lastPress = Time.time;

        switch (pressMode)
        {
            case PressMode.Toggle:
                RequestToggleServerRpc();
                break;
            case PressMode.ForceOn:
                RequestSetServerRpc(true);
                break;
            case PressMode.ForceOff:
                RequestSetServerRpc(false);
                break;
        }
    }

    // ===== eligibility =====
    void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;

        var no = other.GetComponentInParent<NetworkObject>();
        if (!no) return;

        if (!_eligibleClientId.HasValue)
        {
            _eligibleClientId = no.OwnerClientId;
            SetEligibleClientRpc(true, ToClient(_eligibleClientId.Value));
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;

        var no = other.GetComponentInParent<NetworkObject>();
        if (!no) return;

        if (_eligibleClientId.HasValue && no.OwnerClientId == _eligibleClientId.Value)
        {
            SetEligibleClientRpc(false, ToClient(_eligibleClientId.Value));
            _eligibleClientId = null;
        }
    }

    [ClientRpc]
    void SetEligibleClientRpc(bool enable, ClientRpcParams p = default)
    {
        _isLocalEligible = enable;
        if (promptUI) promptUI.SetActive(enable);
    }

    ClientRpcParams ToClient(ulong id) => new ClientRpcParams
    {
        Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { id } }
    };

    // ====== Server RPCs ======
    [ServerRpc(RequireOwnership = false)]
    void RequestToggleServerRpc(ServerRpcParams sp = default)
    {
        if (!CheckSender(sp)) return;
        _currentEnabledState = !_currentEnabledState;
        ApplyToTargets(_currentEnabledState);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestSetServerRpc(bool on, ServerRpcParams sp = default)
    {
        if (!CheckSender(sp)) return;
        _currentEnabledState = on;
        ApplyToTargets(_currentEnabledState);
    }

    bool CheckSender(ServerRpcParams sp)
    {
        if (!_eligibleClientId.HasValue) return false;
        return sp.Receive.SenderClientId == _eligibleClientId.Value;
    }

    void ApplyToTargets(bool groupOn)
    {
        foreach (var t in targets)
        {
            if (!t.allow || t.activatableComponent == null) continue;

            if (t.activatableComponent is IActivatable act)
            {
                bool want = groupOn;
                if (t.invert) want = !want;
                act.Activate(want);
            }
            else
            {
                Debug.LogWarning($"[{name}] {t.activatableComponent.GetType().Name} ไม่ได้ implement IActivatable");
            }
        }
    }
}
