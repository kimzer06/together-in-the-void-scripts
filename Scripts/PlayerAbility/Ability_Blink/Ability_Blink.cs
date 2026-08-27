using Unity.Cinemachine;
using UnityEngine;
using Unity.Netcode;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using StarterAssets;

[RequireComponent(typeof(PlayerRoleFromLobby))]
public class Ability_Blink : NetworkBehaviour
{
    [Header("Debug")]
    [SerializeField] private BlinkAble found_BlinkAbleObj; // BlinkAble ที่เจอ

    [Header("Refs")]
    [SerializeField] CinemachineVirtualCamera vcam;
    [SerializeField] private Canvas crosshairCanvas;
    [SerializeField] private Camera playerCamera;
    [SerializeField] private Animator animator;
    
    [SerializeField] private GameObject blinkMesh;
    [SerializeField] private SkinnedMeshRenderer blinkMeshRenderer;
    [SerializeField] private Material canBlinkMat;
    [SerializeField] private Material cantBlinkMat;
    
    [Header("Aim Settings")]
    [SerializeField] private float aimMaxDistance = 25f;
    [SerializeField] private Vector3 shoulderOffsetWhenAiming = new Vector3(3f, 0.5f, 0f);
    [SerializeField] private float shoulderLerp = 10f;

    [Header("Raycast Settings")]
    [SerializeField] private LayerMask timeFluxLayerMask = -1;
    [SerializeField] private LayerMask groundLayer = -1; // เพิ่ม ground layer สำหรับ spiritMesh
    [SerializeField] private bool showDebugRay = true;

    [Header("Animator Blink Layer")]
    [SerializeField] private string blinkLayerName = "BlinkAbility";
    [SerializeField] private float blinkLayerLerpSpeed = 15f;

    private int blinkLayerIndex;
    private float targetBlinkWeight = 0f;
    private Cinemachine3rdPersonFollow thirdPersonFollow;
    private Vector3 originalShoulderOffset;
    private bool isAiming = false;
    private ThirdPersonController_Rigidbody _tpc;
    private BlinkAble currentTargetedObj;
    
    // เพิ่มตัวแปรสำหรับการจัดการ blinkMesh
    private Vector3 targetWorldPosition;
    private bool canBlinkToTarget = false;

#if ENABLE_INPUT_SYSTEM
    private InputAction aimAction;
    private InputAction fireAction;
#endif

    void Awake()
    {
        _tpc = GetComponent<ThirdPersonController_Rigidbody>();
#if ENABLE_INPUT_SYSTEM
        var pi = GetComponent<PlayerInput>();
        if (pi)
        {
            aimAction = pi.actions["Aim"];
            fireAction = pi.actions["Fire"];
        }
#endif
        if (crosshairCanvas) crosshairCanvas.gameObject.SetActive(false);

        if (vcam != null)
        {
            thirdPersonFollow = vcam.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
            if (thirdPersonFollow != null)
                originalShoulderOffset = thirdPersonFollow.ShoulderOffset;
        }
        if (playerCamera == null)
            playerCamera = Camera.main;

        if (animator != null)
            blinkLayerIndex = animator.GetLayerIndex(blinkLayerName);

        // เริ่มต้นซ่อน blinkMesh และเก็บ Renderer reference
        blinkMesh.SetActive(false);
    }

    void Update()
    {
        if (!IsOwner) return; // ✅ เจ้าของเท่านั้นควบคุมได้

        HandleAiming();
        UpdateAimTarget();
        
        // เพิ่มการอัปเดตตำแหน่ง blinkMesh เมื่อกำลัง aim
        if (isAiming)
        {
            UpdateBlinkMeshPosition();
        }
        else
        {
            blinkMesh.SetActive(false);
        }

        if (animator != null && blinkLayerIndex >= 0)
        {
            float currentWeight = animator.GetLayerWeight(blinkLayerIndex);
            float newWeight = Mathf.Lerp(currentWeight, targetBlinkWeight, Time.deltaTime * blinkLayerLerpSpeed);
            animator.SetLayerWeight(blinkLayerIndex, newWeight);
        }
    }

#region ---------- Blink Functionality ----------
    public void PerformBlink()
    {
        if (!IsOwner) return;
        if (!isAiming || !canBlinkToTarget) return;

        // วาปไปยังตำแหน่งเป้าหมาย
        transform.position = targetWorldPosition;
        
        // เล่นแอนิเมชัน
        PlayAbilityAnimation();
        Debug.Log($"Blinked to position: {targetWorldPosition}");
    }

    public void FireTimeFlux()
    {
        if (!IsOwner) return;
        if (found_BlinkAbleObj == null || !isAiming) return;

        PlayAbilityAnimation();

        // ✅ ส่ง RPC ไปเซิร์ฟเวอร์แทนการแก้ state ตรง ๆ
        var netObj = found_BlinkAbleObj.GetComponent<NetworkObject>();
        if (netObj != null)
            RequestToggleFluxServerRpc(netObj.NetworkObjectId);
    }

    [ServerRpc]
    private void RequestToggleFluxServerRpc(ulong targetId)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetId, out var netObj)) return;

        var flux = netObj.GetComponent<BlinkAble>();
        if (flux == null) return;
    }

    void PlayAbilityAnimation()
    {
        if (animator)
        {
            animator.SetLayerWeight(blinkLayerIndex, 1f);
            animator.SetTrigger("TimeFlux");
        }
    }
    #endregion
    
    #region ---------- Aiming ----------
    void HandleAiming()
    {
#if ENABLE_INPUT_SYSTEM
        if (aimAction != null)
        {
            bool currentlyAiming = aimAction.IsPressed();
            // ป้องกันเปิด crosshair ขณะ pause menu เปิดอยู่
            if (_tpc && _tpc.MovementLocked) currentlyAiming = false;
            if (currentlyAiming != isAiming)
            {
                isAiming = currentlyAiming;
                if (isAiming)
                    StartAiming();
                else
                    StopAiming();
            }

            if (thirdPersonFollow != null)
            {
                Vector3 targetOffset = isAiming ? shoulderOffsetWhenAiming : originalShoulderOffset;
                thirdPersonFollow.ShoulderOffset = Vector3.Lerp(
                    thirdPersonFollow.ShoulderOffset,
                    targetOffset,
                    shoulderLerp * Time.deltaTime
                );
            }
        }
#endif
    }

    void StartAiming()
    {
        if (crosshairCanvas) crosshairCanvas.gameObject.SetActive(true);
        targetBlinkWeight = 1f;
    }

    void StopAiming()
    {
        if (crosshairCanvas) crosshairCanvas.gameObject.SetActive(false);

        if (currentTargetedObj != null)
        {
            currentTargetedObj = null;
            found_BlinkAbleObj = null;
        }
        targetBlinkWeight = 0f;
        blinkMesh.SetActive(false);
    }

    void UpdateAimTarget()
    {
        if (!isAiming || playerCamera == null) return;

        Ray ray = playerCamera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        if (showDebugRay) Debug.DrawRay(ray.origin, ray.direction * aimMaxDistance, Color.blue, 0.1f);

        RaycastHit hit;
        BlinkAble detectedFluxAble = null;

        if (Physics.Raycast(ray, out hit, aimMaxDistance, timeFluxLayerMask))
        {
            detectedFluxAble = hit.collider.GetComponent<BlinkAble>();
            if (detectedFluxAble == null)
                detectedFluxAble = hit.collider.GetComponentInParent<BlinkAble>();
        }
        
        found_BlinkAbleObj = detectedFluxAble;
    }
#endregion

#region ---------- Blink Mesh Position Update ----------
    /// <summary>
    /// อัปเดตตำแหน่ง blinkMesh ตามตำแหน่งที่เล็งและเปลี่ยน Material ตามสถานะ
    /// </summary>
    void UpdateBlinkMeshPosition()
    {
        if (playerCamera == null)
            return;

        // สร้าง Ray จากกล้องไปตามจุดกลางหน้าจอ (crosshair)
        Ray ray = playerCamera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        RaycastHit hit;
        // ตรวจสอบว่า Ray ชนกับ groundLayer หรือไม่
        if (Physics.Raycast(ray, out hit, aimMaxDistance, groundLayer))
        {
            targetWorldPosition = hit.point;
            // ตรวจสอบว่าสามารถ Blink ได้หรือไม่
            BlinkAble hitBlinkAble = hit.collider.GetComponent<BlinkAble>();
            if (hitBlinkAble == null)
                hitBlinkAble = hit.collider.GetComponentInParent<BlinkAble>();
            // สามารถ Blink ได้เฉพาะกับ BlinkAbleGround
            canBlinkToTarget = hitBlinkAble != null && hitBlinkAble is BlinkAbleGround;

            // แสดง blinkMesh
            blinkMesh.SetActive(true);
            blinkMesh.transform.position = targetWorldPosition;
            // หมุน mesh ให้หันหน้าไปทางผู้เล่น
            if (transform != null)
            {
                Vector3 lookDirection = (transform.position - targetWorldPosition).normalized;
                // ตัดทอนแกน Y ออก เพื่อไม่ให้ก้มเงย
                lookDirection.y = 0f;
                if (lookDirection != Vector3.zero)
                {
                    blinkMesh.transform.rotation = Quaternion.LookRotation(lookDirection);
                }
            }
            // เปลี่ยน Material ตามสถานะ
            UpdateBlinkMeshMaterial(canBlinkToTarget);
        }
        else
        {
            // ถ้าเล็งไม่ชนกับ ground ให้ซ่อน blinkMesh
            blinkMesh.SetActive(false);
            canBlinkToTarget = false;
        }
    }
    
    /// <summary>
    /// อัปเดต Material ของ blinkMesh ตามสถานะว่าสามารถ Blink ได้หรือไม่
    /// </summary>
    void UpdateBlinkMeshMaterial(bool canBlink)
    {
        if (blinkMeshRenderer == null) return;
        blinkMeshRenderer.material = canBlink ? canBlinkMat : cantBlinkMat;
    }
    #endregion

    #region ---------- Input Bind ----------
    void OnEnable()
    {
#if ENABLE_INPUT_SYSTEM
        if (fireAction != null)
            fireAction.performed += OnFirePerformed;
#endif
    }

    void OnDisable()
    {
#if ENABLE_INPUT_SYSTEM
        if (fireAction != null)
            fireAction.performed -= OnFirePerformed;
#endif
    }

#if ENABLE_INPUT_SYSTEM
    void OnFirePerformed(InputAction.CallbackContext context)
    {
        PerformBlink();
    }
#endif
#endregion
}