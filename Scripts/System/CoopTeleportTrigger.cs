using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System.Collections;
using StarterAssets; 
using Unity.Netcode.Components; // <-- 1. (สำคัญมาก!) เพิ่ม Using นี้
using UnityEngine.Rendering;
using DG.Tweening;
using TMPro;

[RequireComponent(typeof(Collider))]
public class CoopTeleportTrigger : NetworkBehaviour
{
    [Header("Teleport Destinations")]
    [SerializeField] private Transform roleA_SpawnPoint;
    [SerializeField] private Transform roleB_SpawnPoint;

    [Header("Visuals (Fade Timing)")]
    [SerializeField] private float fadeOutTime = 0.8f;
    [SerializeField] private float blackScreenTime = 0.5f;
    [SerializeField] private float fadeInTime = 0.8f;
    [SerializeField] private float delayBeforeTeleport = 0.2f;

    [Header("UI & Feedback")]
    [SerializeField] private GameObject teleportUI;
    [SerializeField] private TMP_Text playersCountText;
    [SerializeField] private float uiScaleDuration = 0.35f;
    [SerializeField] private float uiScaleOvershoot = 1.7f;

    private readonly Dictionary<ulong, NetworkObject> _clientsInside = new();
    private bool _busy;
    private readonly NetworkVariable<int> _playersInZoneNV = new(0);
    private Coroutine _teleportRoutine;
    private bool _fadeOutIssued;

    private Vector3 _uiOriginalScale;
    private Camera _mainCamera;

    // (Awake, OnTriggerEnter/Exit, TryTransit, TeleportAndFadeInRoutine, Fade RPCs... 
    // ...ส่วนนี้เหมือนเดิมทุกประการครับ)
    #region --- Standard Logic (No Changes) ---
    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        if (delayBeforeTeleport >= (fadeOutTime + blackScreenTime))
        {
            delayBeforeTeleport = fadeOutTime * 0.5f; 
            Debug.LogWarning($"[CoopTeleportTrigger] 'Delay Before Teleport' (เวลาวาร์ป) ต้องน้อยกว่า (Fade Out Time + Black Screen Time) (เวลาก่อนจอเริ่มสว่าง) | ถูกปรับค่าอัตโนมัติเป็น {delayBeforeTeleport} วินาที", this);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        _playersInZoneNV.OnValueChanged += OnPlayersInZoneChanged;
        
        if (teleportUI != null)
        {
            _uiOriginalScale = teleportUI.transform.localScale;
            teleportUI.transform.localScale = Vector3.zero;
            teleportUI.SetActive(false);
            UpdateUIText(0);
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        _playersInZoneNV.OnValueChanged -= OnPlayersInZoneChanged;
    }

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!IsClient) return;
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (camera == _mainCamera && _mainCamera != null && teleportUI != null && teleportUI.activeSelf)
        {
            teleportUI.transform.rotation = _mainCamera.transform.rotation;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || _busy) return;
        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null || !no.IsPlayerObject) return;
        _clientsInside[no.OwnerClientId] = no;
        _playersInZoneNV.Value = _clientsInside.Count;
        TryTransit();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;
        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null || !no.IsPlayerObject) return;
        _clientsInside.Remove(no.OwnerClientId);
        _playersInZoneNV.Value = _clientsInside.Count;

        // ถ้าเริ่มกระบวนการแล้ว แต่มีคนเดินออกจนไม่ครบ 2 ให้ยกเลิก (กันวาร์ปเดี่ยว + กัน coroutine รันต่อ)
        if (_busy && _clientsInside.Count < 2)
        {
            CancelTransitAndFadeIn();
        }
    }

    private void TryTransit()
    {
        if (_busy || _clientsInside.Count < 2) return;

        if (roleA_SpawnPoint == null || roleB_SpawnPoint == null)
        {
            Debug.LogError("[CoopTeleportTrigger] ยังไม่ได้ตั้งค่า roleA_SpawnPoint หรือ roleB_SpawnPoint ใน Inspector!", this);
            return;
        }

        _busy = true;
        _fadeOutIssued = true;
        FadeOutAllClientsClientRpc(fadeOutTime);
        _teleportRoutine = StartCoroutine(TeleportAndFadeInRoutine());
    }

    private IEnumerator TeleportAndFadeInRoutine()
    {
        yield return new WaitForSeconds(delayBeforeTeleport);

        // ถ้าระหว่างรอมีคนเดินออกจนไม่ครบ 2 -> cancel (กันวาร์ปเดี่ยว)
        if (_clientsInside.Count < 2)
        {
            CancelTransitAndFadeIn();
            yield break;
        }

        // Snapshot กันกรณี OnTriggerExit แก้ Dictionary ระหว่างวนลูป
        var snapshot = new List<KeyValuePair<ulong, NetworkObject>>(_clientsInside);
        foreach (var entry in snapshot)
        {
            ulong clientId = entry.Key;
            NetworkObject playerNO = entry.Value;
            if (playerNO == null) continue;

            if (playerNO.TryGetComponent<PlayerRoleFromLobby>(out var role))
            {
                Transform targetSpawn = (role.Role.Value == PlayerRole.RoleA) 
                    ? roleA_SpawnPoint 
                    : roleB_SpawnPoint;

                ClientRpcParams rpcParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } 
                };
                
                TeleportPlayerClientRpc(targetSpawn.position, targetSpawn.rotation, rpcParams);
            }
            else
            {
                Debug.LogWarning($"[CoopTeleportTrigger] ผู้เล่น {playerNO.name} ไม่มี PlayerRoleFromLobby component!", playerNO);
            }
        }
        
        _clientsInside.Clear(); 
        _playersInZoneNV.Value = 0;
        float remainingWaitBeforeFadeIn = (fadeOutTime + blackScreenTime) - delayBeforeTeleport;
        
        if (remainingWaitBeforeFadeIn > 0)
        {
            yield return new WaitForSeconds(remainingWaitBeforeFadeIn);
        }
        
        FadeInAllClientsClientRpc(fadeInTime);
        yield return new WaitForSeconds(fadeInTime); 
        _busy = false; 
        _teleportRoutine = null;
        _fadeOutIssued = false;
    }

    private void CancelTransitAndFadeIn()
    {
        if (!IsServer) return;

        if (_teleportRoutine != null)
        {
            StopCoroutine(_teleportRoutine);
            _teleportRoutine = null;
        }

        // ถ้าเคยสั่ง FadeOut ไปแล้ว ให้สั่ง FadeIn กลับ (ไม่งั้นจอดำค้าง)
        if (_fadeOutIssued)
        {
            FadeInAllClientsClientRpc(fadeInTime);
        }

        _busy = false;
        _fadeOutIssued = false;
    }

    [ClientRpc]
    private void FadeOutAllClientsClientRpc(float duration)
    {
        if (ScreenFader.I != null)
            ScreenFader.I.FadeOut(duration);
    }

    [ClientRpc]
    private void FadeInAllClientsClientRpc(float duration)
    {
        if (ScreenFader.I != null)
            ScreenFader.I.FadeIn(duration);
    }

    #region UI Effects
    private void OnPlayersInZoneChanged(int previous, int current)
    {
        UpdateUI(current);
    }

    private void UpdateUI(int count)
    {
        if (teleportUI == null) return;
        
        UpdateUIText(count);
        
        if (count > 0)
        {
            ScaleShow(teleportUI, _uiOriginalScale);
        }
        else
        {
            ScaleHide(teleportUI);
        }
    }

    private void UpdateUIText(int count)
    {
        if (playersCountText != null)
        {
            playersCountText.text = $"{count}/2";
        }
    }

    private void ScaleShow(GameObject go, Vector3 targetScale)
    {
        if (go == null) return;
        go.transform.DOKill();
        go.SetActive(true);
        go.transform.DOScale(targetScale, uiScaleDuration)
            .SetEase(Ease.OutBack, uiScaleOvershoot);
    }

    private void ScaleHide(GameObject go)
    {
        if (go == null) return;
        go.transform.DOKill();
        go.transform.DOScale(Vector3.zero, uiScaleDuration)
            .SetEase(Ease.InBack, uiScaleOvershoot)
            .OnComplete(() => go.SetActive(false));
    }
    #endregion

    #endregion

    [ClientRpc]
    private void TeleportPlayerClientRpc(Vector3 position, Quaternion rotation, ClientRpcParams rpcParams = default)
    {
        NetworkObject localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
        if (localPlayer == null) 
        {
            Debug.LogError($"[Client {NetworkManager.Singleton.LocalClientId}] ได้รับคำสั่งวาร์ป แต่หา LocalClient.PlayerObject ไม่เจอ!");
            return;
        }
        
        Debug.Log($"[Client {NetworkManager.Singleton.LocalClientId}] เริ่มกระบวนการวาร์ป (Teleport) (T = {Time.time})");
        StartCoroutine(SafeTeleportLocalPlayer_Coroutine(localPlayer, position, rotation));
    }

    // --- 2. แก้ไข Coroutine นี้เป็นครั้งสุดท้าย (ใช้ NetworkTransform.Teleport) ---
    private IEnumerator SafeTeleportLocalPlayer_Coroutine(NetworkObject playerNO, Vector3 pos, Quaternion rot)
    {
        // --- 1. Get Components ---
        playerNO.TryGetComponent<ThirdPersonController_Rigidbody>(out var controller);
        playerNO.TryGetComponent<Rigidbody>(out var rb);
        
        // (สำคัญ!) ดึง ClientNetworkTransform
        playerNO.TryGetComponent<ClientNetworkTransform>(out var netTransform);
        
        Collider[] allColliders = playerNO.GetComponentsInChildren<Collider>(true); 
        bool[] colliderStates = new bool[allColliders.Length];

        // --- 2. Disable everything ---
        if (controller) controller.enabled = false;
        
        for (int i = 0; i < allColliders.Length; i++)
        {
            if (allColliders[i])
            {
                colliderStates[i] = allColliders[i].enabled;
                allColliders[i].enabled = false;
            }
        }

        bool wasKinematic = false;
        if (rb)
        {
            wasKinematic = rb.isKinematic;
            rb.isKinematic = true; 
            rb.interpolation = RigidbodyInterpolation.None; // ปิด RB Interpolation
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // --- 3. Wait for Physics ---
        yield return new WaitForFixedUpdate(); 

        // --- 4. Teleport (The "Bulletproof" way) ---
        if (netTransform != null)
        {
            // (นี่คือหัวใจ!) สั่ง Teleport ที่ NetworkTransform
            // มันจะปิด Network Interpolation ของตัวเอง 1 ครั้ง
            netTransform.Teleport(pos, rot, Vector3.one);
        }
        else
        {
            // Fallback (เผื่อไม่มี ClientNetworkTransform)
            playerNO.transform.SetPositionAndRotation(pos, rot);
        }

        // (ยังคงต้องอัปเดต transform/rb ด้วยตนเอง เผื่อกรณี Teleport() ไม่ได้ทำ)
        playerNO.transform.SetPositionAndRotation(pos, rot);
        if (rb)
        {
            rb.position = pos;
            rb.rotation = rot;
        }

        // --- 5. Wait for Physics again ---
        yield return new WaitForFixedUpdate(); 

        // --- 6. Re-enable everything ---
        for (int i = 0; i < allColliders.Length; i++)
        {
            if (allColliders[i])
            {
                allColliders[i].enabled = colliderStates[i];
            }
        }
        
        if (rb)
        {
            // คืนค่า Interpolation (ตาม TPC)
            rb.interpolation = RigidbodyInterpolation.Interpolate; 
            rb.isKinematic = wasKinematic; 
        }
        
        if (controller) 
        {
            controller.enabled = true;
            // สแน็ปกล้องไปด้านหลังตัวละครหลังวาร์ป (reset pitch ด้วย)
            controller.SnapCameraBehindCharacter(keepPitch: false);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        DrawSpawnGizmo(roleA_SpawnPoint, "Role A", new Color(0.2f, 0.6f, 1f));
        DrawSpawnGizmo(roleB_SpawnPoint, "Role B", new Color(1f, 0.4f, 0.3f));
    }

    private void DrawSpawnGizmo(Transform spawnPoint, string label, Color color)
    {
        if (spawnPoint == null) return;

        Vector3 pos = spawnPoint.position;
        Vector3 forward = spawnPoint.forward;
        Vector3 right = spawnPoint.right;

        float arrowLength = 1.5f;
        float arrowHeadLength = 0.3f;
        float arrowHeadAngle = 25f;

        // Arrow shaft
        Gizmos.color = color;
        Gizmos.DrawLine(pos, pos + forward * arrowLength);

        // Arrowhead
        Vector3 arrowTip = pos + forward * arrowLength;
        Vector3 headRight = Quaternion.LookRotation(forward) * Quaternion.Euler(0, 180 + arrowHeadAngle, 0) * Vector3.forward;
        Vector3 headLeft  = Quaternion.LookRotation(forward) * Quaternion.Euler(0, 180 - arrowHeadAngle, 0) * Vector3.forward;
        Gizmos.DrawLine(arrowTip, arrowTip + headRight * arrowHeadLength);
        Gizmos.DrawLine(arrowTip, arrowTip + headLeft  * arrowHeadLength);

        // Spawn position sphere
        Gizmos.DrawWireSphere(pos, 0.25f);

        // Label
        GUIStyle style = new GUIStyle();
        style.normal.textColor = color;
        style.fontStyle = FontStyle.Bold;
        style.fontSize = 12;
        UnityEditor.Handles.Label(pos + Vector3.up * 0.5f, $"{label} Forward →", style);
    }
#endif
}