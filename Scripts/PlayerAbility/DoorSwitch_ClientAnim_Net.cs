using Unity.Netcode;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// DoorSwitch_ClientAnim_Net
/// - วางบน "สวิตช์" (GameObject ที่มี Collider isTrigger)
/// - เมื่อ Player (tag = "Player") เข้า trigger จะแสดง UI ให้เฉพาะไคลเอนต์เจ้าของผู้เล่นคนนั้น
/// - ผู้เล่นกด E -> ส่งคำขอไป Server -> Server อนุมัติ -> ส่ง ClientRpc ให้ทุกเครื่อง SetTrigger("Open")
/// หมายเหตุ: ใช้กับประตูที่มี Animator + ClientNetworkAnimator (บน GameObject เดียวกัน) และมี NetworkObject
/// </summary>
public class DoorSwitch_ClientAnim_Net : NetworkBehaviour
{
    [Header("Detection")]
    [SerializeField] private string playerTag = "Player";

    [Header("UI Prompt (แสดงเฉพาะคนที่อยู่ในระยะ)")]
    [SerializeField] private GameObject promptUI;

    [Header("Door Animator (ใช้กับ ClientNetworkAnimator)")]
    [SerializeField] private Animator doorAnimator;     // Animator ของประตู (บน GO เดียวกับ ClientNetworkAnimator)
    [SerializeField] private string openTriggerName = "Open";

    [Header("Input (fallback เมื่อไม่ใช้ New Input System)")]
    [SerializeField] private KeyCode fallbackKey = KeyCode.E;

    // Server จะจำ client ที่มีสิทธิกด ณ ตอนนี้
    private ulong? eligibleClientId = null;

    // ฝั่งไคลเอนต์: บอกว่า "ฉัน" อยู่ในระยะและเห็นปุ่มกด
    private bool isLocalEligible = false;

    private void Awake()
    {
        if (promptUI != null) promptUI.SetActive(false);
    }

    private void Update()
    {
        if (!IsClient || !isLocalEligible) return;

        bool pressed = false;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
            pressed = Keyboard.current.eKey.wasPressedThisFrame;
#else
        pressed = Input.GetKeyDown(fallbackKey);
#endif
        if (pressed)
        {
            RequestOpenServerRpc();
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;

        var netObj = other.GetComponentInParent<NetworkObject>();
        if (netObj == null) return;

        if (eligibleClientId.HasValue) return; // ล็อกสิทธิ์ทีละคนเพื่อกันสแปม
        eligibleClientId = netObj.OwnerClientId;

        // บอกเฉพาะไคลเอนต์เจ้าของให้โชว์ UI
        SetEligibleClientRpc(true, ToClient(eligibleClientId.Value));
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;
        if (!other.CompareTag(playerTag)) return;

        var netObj = other.GetComponentInParent<NetworkObject>();
        if (netObj == null) return;

        if (eligibleClientId.HasValue && netObj.OwnerClientId == eligibleClientId.Value)
        {
            SetEligibleClientRpc(false, ToClient(eligibleClientId.Value));
            eligibleClientId = null;
        }
    }

    [ClientRpc]
    private void SetEligibleClientRpc(bool enable, ClientRpcParams clientRpcParams = default)
    {
        isLocalEligible = enable;
        if (promptUI != null) promptUI.SetActive(enable);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestOpenServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (!eligibleClientId.HasValue) return;

        ulong sender = serverRpcParams.Receive.SenderClientId;
        if (sender != eligibleClientId.Value) return;

        // อนุมัติแล้ว: ส่งสัญญาณไป "ทุกไคลเอนต์" ให้ SetTrigger(Open) ที่ Animator ของตัวเอง
        PlayOpenTriggerClientRpc();
    }

    // ทุกเครื่องจะเซ็ต trigger "Open" บน Animator โลคัลของตัวเอง
    [ClientRpc]
    private void PlayOpenTriggerClientRpc()
    {
        if (doorAnimator != null && !string.IsNullOrEmpty(openTriggerName))
        {
            doorAnimator.SetTrigger(openTriggerName);
        }
        else
        {
            Debug.LogWarning("[DoorSwitch_ClientAnim_Net] doorAnimator หรือ openTriggerName ไม่ถูกตั้งค่า");
        }
    }

    private ClientRpcParams ToClient(ulong clientId)
    {
        return new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new ulong[] { clientId }
            }
        };
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (doorAnimator == null)
        {
            var found = GetComponentInChildren<Animator>();
            if (found != null) doorAnimator = found;
        }
    }
#endif
}